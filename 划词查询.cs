using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Glosser
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 启用 TLS 1.2，否则 https 接口会握手失败
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            bool createdNew;
            Mutex mutex = new Mutex(true, "Glosser_Global_Hotkey_App", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("划词查询已经在运行中。\n若刚才启动过旧版本，请先右键系统托盘图标选择\"退出\"，\n确认托盘图标消失后再启动新版。", "划词查询", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());

            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    // ==================== 配置数据 ====================
    public class Settings
    {        public bool EnableAI = true;
        public string BaseUrl = "https://api.openai.com/v1/chat/completions";
        public string ApiKey = "";
        public string Model = "gpt-4o-mini";
        public int TimeoutSec = 20;
        public int DoublePressMs = 800;   // 保留字段兼容旧配置，v0.4 起不再使用
        public int BubbleSeconds = 10;
        public int QueryCooldownSec = 3;  // 同词重复查询冷却（秒）
        public bool EnableCache = true;   // 查询结果缓存
        public int CacheHours = 24;       // 缓存有效期（小时）
        public int QueryModifiers = 3;    // MOD_ALT|MOD_CONTROL
        public int QueryVk = 0x51;        // Q
        public Dictionary<string, string> Dict = new Dictionary<string, string>();

        // 把修饰符+键码转成可读名称，如 "Ctrl+Alt+Q"
        public static string HotkeyName(int mods, int vk)
        {
            string s = "";
            if ((mods & 0x0002) != 0) s += "Ctrl+";
            if ((mods & 0x0001) != 0) s += "Alt+";
            if ((mods & 0x0004) != 0) s += "Shift+";
            if (vk >= 0x30 && vk <= 0x39) s += (char)('0' + (vk - 0x30));
            else if (vk >= 0x41 && vk <= 0x5A) s += (char)vk;
            else if (vk >= 0x70 && vk <= 0x87) s += "F" + (vk - 0x6F);
            else s += ((Keys)vk).ToString();
            return s;
        }

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    return js.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
                }
            }
            catch { }
            return new Settings();
        }

        public void Save(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            File.WriteAllText(path, js.Serialize(this), new UTF8Encoding(false));
        }
    }

    // 缓存条目
    public class CacheEntry
    {
        public string Result;
        public string Model;
        public DateTime Time;
    }

    // ==================== 主窗体（驻留托盘 + 热键） ====================
    public class MainForm : Form
    {
        const string VERSION = "1.1";

        const int WM_HOTKEY = 0x0312;
        const int MOD_CONTROL = 0x0002;
        const int MOD_ALT = 0x0001;
        const int MOD_NOREPEAT = 0x4000;
        const int VK_C = 0x43;              // 模拟复制用
        const int VK_Q = 0x51;
        const int HOTKEY_ID_QUERY = 0x8A02;  // Ctrl+Alt+Q 查询热键（唯一）

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 模拟按键（SendInput）用到的定义
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_MENU = 0x12;    // Alt
        const ushort VK_SHIFT = 0x10;
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
        }

        private NotifyIcon tray;
        private Settings settings;
        private string cfgPath;
        private string cachePath;
        private Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();
        private object cacheLock = new object();
        private BubbleForm bubble;
        private SettingsForm activeSettings;
        private System.Windows.Forms.Timer queryTimer;
        private uint lastSeqBeforeCopy = 0;
        private DateTime lastHotkeyTime = DateTime.MinValue; // 防连发
        private string lastQueriedTerm = "";                  // 同词冷却
        private DateTime lastTermTime = DateTime.MinValue;

        public MainForm()
        {
            cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            settings = Settings.Load(cfgPath);
            cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache.json");
            cache = LoadCache();
            if (settings.Dict.Count == 0)
            {
                // 首次运行给几个示例词条，方便体验
                settings.Dict["后掠翼"] = "机翼向后倾斜的构型，降低高速飞行时的空气阻力，代价是低速机动性变差、起降速度更高。";
                settings.Dict["推重比"] = "推力与重量的比值，大于 1 意味着可以垂直爬升，是衡量战机性能的核心指标之一。";
                settings.Dict["雷达散射截面"] = "目标被雷达\u201c看见\u201d的难易程度，单位平方米。数值越小越\u201c隐形\u201d。";
            }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;

            BuildTray();
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            try
            {
                tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
            if (tray.Icon == null) tray.Icon = SystemIcons.Application;
            tray.Text = "划词查询：划词后按 Ctrl+Alt+Q";
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("设置", null, delegate { ShowSettings(); });
            menu.Items.Add("测试查询", null, delegate { TestQuery(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, delegate { ShowAbout(); });
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
        }

        private void ShowAbout()
        {
            MessageBox.Show("划词查询 v" + VERSION + "\n\n选中文字后连按两次 Ctrl+C 查询。\n程序文件: " + Application.ExecutablePath, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 写运行日志（用于排查），失败静默
        private void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [v" + VERSION + "] " + msg + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hide();

            // 查询热键触发后：模拟复制 → 250ms 后读剪贴板并检测复制是否生效
            queryTimer = new System.Windows.Forms.Timer();
            queryTimer.Interval = 250;
            queryTimer.Tick += delegate
            {
                queryTimer.Stop();
                uint seqNow = GetClipboardSequenceNumber();
                string text = GetClipboardText();
                bool copied = (seqNow != lastSeqBeforeCopy);
                Log("读取剪贴板: " + (text.Length > 40 ? text.Substring(0, 40) + "..." : text)
                    + " | 序列号 " + lastSeqBeforeCopy + "→" + seqNow + " | 检测到新复制: " + copied);
                if (!string.IsNullOrEmpty(text) && text.Length >= 2 && text.Length <= 300)
                {
                    if (!copied)
                    {
                        // 没检测到新复制：可能自动复制失败，也可能用户只是查剪贴板现有内容。
                        // 仍查询剪贴板内容，但明确标注来源，避免"查旧词"的困惑。
                        Log("警告: 未检测到新复制，查询剪贴板现有内容");
                        ThreadPool.QueueUserWorkItem(delegate { QueryAndShow(text, "未检测到划词复制，以下为剪贴板现有内容的解释"); });
                        return;
                    }
                    if (CoolingDown(text)) return; // 同词冷却
                    ThreadPool.QueueUserWorkItem(delegate { QueryAndShow(text, null); });
                }
                else
                {
                    ShowBubble("剪贴板无可查内容", "请先选中文字，再按查询热键查询。", "INFO");
                }
            };

            ApplyHotkey();

            // 首次运行（还没有配置文件）自动打开设置窗
            if (!File.Exists(cfgPath))
            {
                BeginInvoke(new Action(ShowSettings));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKey(Handle, HOTKEY_ID_QUERY);
            if (queryTimer != null) { queryTimer.Stop(); queryTimer.Dispose(); }
            if (bubble != null) { try { bubble.Close(); } catch { } }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID_QUERY)
            {
                OnQueryKey();
            }
            base.WndProc(ref m);
        }

        // Ctrl+C 被全局热键劫持后，前台程序收不到按键、无法复制。
        // 这里程序自己模拟一次 Ctrl+C 发给前台窗口，把"复制"补上，
        // 同时保证剪贴板同步成当前选中内容。
        // 注入 Ctrl+C 复制选中文字。
        // 用 keybd_event：SendInput 在本机被阻塞（返回0，错误码不可靠），
        // keybd_event 更宽容，不检查"已按下"冲突。
        // 仍然：先松开 Alt/Shift 残留，Ctrl 未按才补按，避免状态粘滞。
        private void SimulateCopy()
        {
            try
            {
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                if (altDown) keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (shiftDown) keybd_event((byte)VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (!ctrlDown) keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event((byte)VK_C, 0, 0, UIntPtr.Zero);
                keybd_event((byte)VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (!ctrlDown) keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log("SimulateCopy 异常: " + ex.Message);
            }
        }

        // 查询热键：模拟复制选中文字 → 读剪贴板 → 查询
        private void OnQueryKey()
        {
            // 防连发：600ms 内忽略重复触发
            DateTime now = DateTime.Now;
            if ((now - lastHotkeyTime).TotalMilliseconds < 600)
            {
                Log("忽略：热键连发");
                return;
            }
            lastHotkeyTime = now;

            lastSeqBeforeCopy = GetClipboardSequenceNumber(); // 先赋值再记录
            Log("查询热键按下，复制前序列号=" + lastSeqBeforeCopy + "，" + GetForegroundInfo());
            SimulateCopy();
            queryTimer.Stop();
            queryTimer.Start();
        }

        // 当前前台窗口的进程名和标题（诊断用）
        private string GetForegroundInfo()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string proc = "";
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    proc = p.ProcessName;
                }
                return "前台窗口: [" + proc + "] " + sb.ToString();
            }
            catch { return "前台窗口获取失败"; }
        }

        // 同词冷却：冷却期内不重复查询同一个词，避免反复花钱
        private bool CoolingDown(string term)
        {
            DateTime now = DateTime.Now;
            if (term == lastQueriedTerm && (now - lastTermTime).TotalSeconds < settings.QueryCooldownSec)
            {
                Log("忽略：同词冷却 " + term);
                return true;
            }
            lastQueriedTerm = term;
            lastTermTime = now;
            return false;
        }

        private string GetClipboardText()
        {
            try { return Clipboard.GetText().Trim(); }
            catch { return ""; }
        }

        // ===== 查询缓存：查过的词条在有效期内直接复用，省 Token =====
        private Dictionary<string, CacheEntry> LoadCache()
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    return js.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cachePath, Encoding.UTF8));
                }
            }
            catch { }
            return new Dictionary<string, CacheEntry>();
        }

        private void SaveCache()
        {
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                File.WriteAllText(cachePath, js.Serialize(cache), new UTF8Encoding(false));
            }
            catch { }
        }

        // 查询流程：本地词典优先，AI 兜底。note 为来源说明（如"剪贴板现有内容"）
        private void QueryAndShow(string term, string note)
        {
            Log("查询词: " + term + (note != null ? " [" + note + "]" : ""));
            string prefix = note != null ? "(" + note + ")\n\n" : "";
            if (settings.Dict.ContainsKey(term))
            {
                Log("结果: 本地词典命中");
                ShowBubble(term, prefix + settings.Dict[term], "LOCAL DICTIONARY");
                return;
            }
            if (!settings.EnableAI)
            {
                Log("结果: 本地词典未命中且 AI 未启用");
                ShowBubble(term, prefix + "本地词典里没有这个词，且 AI 查询未启用。\n请在设置里启用 AI，或把词条加进本地词典。", "LOCAL ONLY");
                return;
            }
            // 查缓存（有效期内直接复用）
            if (settings.EnableCache)
            {
                lock (cacheLock)
                {
                    CacheEntry ce = null;
                    if (cache.TryGetValue(term, out ce))
                    {
                        if ((DateTime.Now - ce.Time).TotalHours < settings.CacheHours)
                        {
                            Log("结果: 缓存命中 (" + ce.Model + ")");
                            ShowBubble(term, prefix + ce.Result, "CACHE · " + ce.Model);
                            return;
                        }
                        cache.Remove(term);
                        Log("结果: 缓存过期移除 " + term);
                    }
                }
            }
            // 走 AI
            ShowBubble(term, prefix + "查询中…", "STANDBY");
            string result = QueryAI(term);
            if (settings.EnableCache && !string.IsNullOrEmpty(result) && !result.StartsWith("查询失败"))
            {
                lock (cacheLock)
                {
                    cache[term] = new CacheEntry { Result = result, Model = settings.Model, Time = DateTime.Now };
                    // 上限 2000 条，超出移除最旧的，防止文件膨胀
                    if (cache.Count > 2000)
                    {
                        List<string> oldest = cache.OrderBy(kv => kv.Value.Time).Take(500).Select(kv => kv.Key).ToList();
                        foreach (string k in oldest) cache.Remove(k);
                    }
                    SaveCache();
                }
            }
            Log("结果: AI 查询完成");
            ShowBubble(term, prefix + result, "AI · " + settings.Model);
        }

        // 调用 OpenAI 兼容接口（地址自动补全 + 详细错误诊断）
        private string QueryAI(string term)
        {
            string finalUrl = NormalizeBaseUrl(settings.BaseUrl);
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = settings.Model;

                List<Dictionary<string, string>> messages = new List<Dictionary<string, string>>();
                Dictionary<string, string> sys = new Dictionary<string, string>();
                sys["role"] = "system";
                sys["content"] = "你是专业的词条解释助手。用户给你一个词语或短语，请用简洁、准确的中文解释它在常见专业语境下的含义，200字以内，直接输出解释正文，不要客套话。";
                messages.Add(sys);

                Dictionary<string, string> usr = new Dictionary<string, string>();
                usr["role"] = "user";
                usr["content"] = term;
                messages.Add(usr);

                payload["messages"] = messages;
                payload["temperature"] = 0.3;
                payload["max_tokens"] = 400;

                JavaScriptSerializer js = new JavaScriptSerializer();
                string body = js.Serialize(payload);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(finalUrl);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = settings.TimeoutSec * 1000;
                req.Headers["Authorization"] = "Bearer " + settings.ApiKey;
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    try
                    {
                        Dictionary<string, object> obj = (Dictionary<string, object>)js.DeserializeObject(json);
                        object[] choices = (object[])obj["choices"];
                        Dictionary<string, object> first = (Dictionary<string, object>)choices[0];
                        Dictionary<string, object> msg = (Dictionary<string, object>)first["message"];
                        return ((string)msg["content"]).Trim();
                    }
                    catch
                    {
                        return "响应格式异常，服务端返回前 300 字:\n" + (json.Length > 300 ? json.Substring(0, 300) : json);
                    }
                }
            }
            catch (WebException wex)
            {
                string status = "";
                string detail = "";
                if (wex.Response != null)
                {
                    try
                    {
                        HttpWebResponse r = (HttpWebResponse)wex.Response;
                        status = " HTTP " + (int)r.StatusCode;
                        using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        {
                            detail = sr.ReadToEnd();
                        }
                        if (detail.Length > 300) detail = detail.Substring(0, 300);
                    }
                    catch { }
                }
                string head = "查询失败[" + status.Trim() + "]\n请求地址: " + finalUrl;
                if (detail.Length > 0) head += "\n服务端返回: " + detail;
                else head += "\n" + wex.Message;
                return head;
            }
            catch (Exception ex)
            {
                return "查询失败: " + ex.Message + "\n请求地址: " + finalUrl;
            }
        }

        // 地址自动补全：兼容三种填法
        private string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "https://api.openai.com/v1/chat/completions";
            string u = url.Trim().TrimEnd('/');
            if (u.EndsWith("/chat/completions")) return u;
            if (u.EndsWith("/v1")) return u + "/chat/completions";
            return u + "/v1/chat/completions";
        }

        // 在 UI 线程显示气泡（source 用于底部来源栏，如 LOCAL DICTIONARY / AI · 模型名）
        private void ShowBubble(string title, string content, string source)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, string>(ShowBubble), title, content, source);
                return;
            }
            if (bubble != null)
            {
                try { bubble.Close(); } catch { }
                bubble = null;
            }
            bubble = new BubbleForm(title, content, source, settings.BubbleSeconds);
            bubble.Show();
        }

        // 按当前设置注册查询热键
        private void ApplyHotkey()
        {
            UnregisterHotKey(Handle, HOTKEY_ID_QUERY);
            bool ok = RegisterHotKey(Handle, HOTKEY_ID_QUERY, settings.QueryModifiers | MOD_NOREPEAT, settings.QueryVk);
            Log("热键 " + Settings.HotkeyName(settings.QueryModifiers, settings.QueryVk) + " 注册" + (ok ? "成功" : "失败"));
            if (!ok)
            {
                tray.ShowBalloonTip(4000, "划词查询", "热键 " + Settings.HotkeyName(settings.QueryModifiers, settings.QueryVk) + " 注册失败，可能被其他程序占用，请到设置里更换。", ToolTipIcon.Warning);
            }
        }

        private void ShowSettings()
        {
            // 防重入：已有设置窗时只激活，不新建
            if (activeSettings != null)
            {
                try { activeSettings.Activate(); } catch { }
                return;
            }
            UnregisterHotKey(Handle, HOTKEY_ID_QUERY); // 设置期间暂停热键，避免误触发
            activeSettings = new SettingsForm(settings, cfgPath);
            activeSettings.FormClosed += delegate { activeSettings = null; };
            activeSettings.ShowDialog(this);
            activeSettings.Dispose();
            ApplyHotkey(); // 恢复（或应用新热键）
        }

        // 不划词也能验证查询是否通
        private void TestQuery()
        {
            Form f = new Form();
            f.Text = "测试查询";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(380, 120);
            f.Font = new Font("Microsoft YaHei", 9f);
            Label lab = new Label();
            lab.Text = "输入要查询的词：";
            lab.Location = new Point(12, 10);
            lab.AutoSize = true;
            TextBox tb = new TextBox();
            tb.Location = new Point(12, 36);
            tb.Size = new Size(356, 23);
            Button btn = new Button();
            btn.Text = "查询";
            btn.Location = new Point(302, 76);
            btn.Size = new Size(66, 28);
            btn.DialogResult = DialogResult.OK;
            f.Controls.Add(lab);
            f.Controls.Add(tb);
            f.Controls.Add(btn);
            f.AcceptButton = btn;
            f.ShowDialog();
            string term = tb.Text.Trim();
            f.Dispose();
            if (string.IsNullOrEmpty(term)) return;
            ThreadPool.QueueUserWorkItem(delegate { QueryAndShow(term, null); });
        }
    }

    // ==================== 气泡窗体（深色极简太空风） ====================
    public class BubbleForm : Form
    {
        private System.Windows.Forms.Timer closeTimer;
        private System.Windows.Forms.Timer fadeTimer;
        private const int MAX_W = 380;
        private static readonly Color BG = Color.FromArgb(6, 6, 6);
        private static readonly Color LINE = Color.FromArgb(46, 46, 46);
        private static readonly Color BODY = Color.FromArgb(206, 206, 206);
        private static readonly Color TAG = Color.FromArgb(140, 140, 140);

        public BubbleForm(string title, string content, string source, int autoCloseSeconds)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = BG;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(1);
            Opacity = 0; // 淡入起点

            TableLayoutPanel table = new TableLayoutPanel();
            table.ColumnCount = 1;
            table.RowCount = 5;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Dock = DockStyle.Fill;
            table.BackColor = BG;

            // 1) 顶部小号大写标签（宽字距的章节标签风格）
            Label tag = new Label();
            tag.Text = "G L O S S A R Y";
            tag.ForeColor = TAG;
            tag.Font = new Font("Segoe UI", 8.5f);
            tag.AutoSize = true;
            tag.Padding = new Padding(22, 18, 22, 0);
            tag.BackColor = Color.Transparent;
            table.Controls.Add(tag, 0, 0);

            // 2) 词条标题（白色粗体）
            Label titleLbl = new Label();
            titleLbl.Text = title;
            titleLbl.ForeColor = Color.White;
            titleLbl.Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold);
            titleLbl.AutoSize = true;
            titleLbl.Padding = new Padding(22, 3, 22, 0);
            titleLbl.MaximumSize = new Size(MAX_W, 0);
            titleLbl.BackColor = Color.Transparent;
            titleLbl.Cursor = Cursors.Hand;
            titleLbl.Click += delegate { Close(); };
            table.Controls.Add(titleLbl, 0, 1);

            // 3) 细分隔线
            Label line = new Label();
            line.AutoSize = false;
            line.Height = 1;
            line.BackColor = LINE;
            line.Margin = new Padding(22, 10, 22, 6);
            line.Cursor = Cursors.Hand;
            line.Click += delegate { Close(); };
            table.Controls.Add(line, 0, 2);

            // 4) 正文（浅灰、行距宽松）
            Label bodyLbl = new Label();
            bodyLbl.Text = content;
            bodyLbl.ForeColor = BODY;
            bodyLbl.Font = new Font("Microsoft YaHei", 10f);
            bodyLbl.AutoSize = true;
            bodyLbl.Padding = new Padding(22, 4, 22, 14);
            bodyLbl.MaximumSize = new Size(MAX_W, 0);
            bodyLbl.BackColor = Color.Transparent;
            bodyLbl.Cursor = Cursors.Hand;
            bodyLbl.Click += delegate { Close(); };
            table.Controls.Add(bodyLbl, 0, 3);

            // 5) 底部来源栏
            FlowLayoutPanel footer = new FlowLayoutPanel();
            footer.AutoSize = true;
            footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            footer.BackColor = Color.Transparent;
            footer.Margin = new Padding(0);
            footer.Padding = new Padding(22, 0, 22, 14);
            footer.FlowDirection = FlowDirection.LeftToRight;
            footer.Cursor = Cursors.Hand;
            footer.Click += delegate { Close(); };

            Label srcLbl = new Label();
            srcLbl.Text = "SOURCE · " + source;
            srcLbl.ForeColor = TAG;
            srcLbl.Font = new Font("Segoe UI", 8f);
            srcLbl.AutoSize = true;
            srcLbl.BackColor = Color.Transparent;
            srcLbl.Cursor = Cursors.Hand;
            srcLbl.Click += delegate { Close(); };

            Label closeLbl = new Label();
            closeLbl.Text = "CLICK TO CLOSE";
            closeLbl.ForeColor = Color.FromArgb(90, 90, 90);
            closeLbl.Font = new Font("Segoe UI", 8f);
            closeLbl.AutoSize = true;
            closeLbl.BackColor = Color.Transparent;
            closeLbl.Margin = new Padding(18, 0, 0, 0);
            closeLbl.Cursor = Cursors.Hand;
            closeLbl.Click += delegate { Close(); };

            footer.Controls.Add(srcLbl);
            footer.Controls.Add(closeLbl);
            table.Controls.Add(footer, 0, 4);

            Controls.Add(table);

            if (autoCloseSeconds > 0)
            {
                closeTimer = new System.Windows.Forms.Timer();
                closeTimer.Interval = autoCloseSeconds * 1000;
                closeTimer.Tick += delegate { Close(); };
                closeTimer.Start();
            }

            // 淡入动画
            fadeTimer = new System.Windows.Forms.Timer();
            fadeTimer.Interval = 20;
            fadeTimer.Tick += delegate
            {
                if (Opacity >= 1.0)
                {
                    fadeTimer.Stop();
                    return;
                }
                Opacity = Math.Min(1.0, Opacity + 0.16);
            };
        }

        // 无边框弹窗不抢焦点，不打断当前输入
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PositionNearCursor();
            fadeTimer.Start();
        }

        private void PositionNearCursor()
        {
            Point cur = Cursor.Position;
            Screen screen = Screen.FromPoint(cur);
            int x = cur.X + 14;
            int y = cur.Y + 14;
            if (x + Width > screen.WorkingArea.Right - 8) x = cur.X - Width - 14;
            if (x < screen.WorkingArea.Left + 8) x = screen.WorkingArea.Left + 8;
            if (y + Height > screen.WorkingArea.Bottom - 8) y = cur.Y - Height - 14;
            if (y < screen.WorkingArea.Top + 8) y = screen.WorkingArea.Top + 8;
            Location = new Point(x, y);
        }
    }

    // ==================== 设置窗体 ====================
    public class SettingsForm : Form
    {
        private Settings settings;
        private string cfgPath;

        private CheckBox chkEnableAI;
        private CheckBox chkCache;
        private NumericUpDown numCacheHours;
        private TextBox txtBaseUrl;
        private TextBox txtApiKey;
        private TextBox txtModel;
        private NumericUpDown numTimeout;
        private DataGridView dgv;
        private NumericUpDown numBubbleSec;
        private NumericUpDown numCooldown;
        private CheckBox chkStartup;
        private TextBox hotkeyBox;
        private Button btnHotkey;
        private int curMods = 3;
        private int curVk = 0x51;
        private bool capturingHotkey = false;

        public SettingsForm(Settings s, string path)
        {
            settings = s;
            cfgPath = path;

            Text = "划词查询 · 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(580, 560);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Microsoft YaHei", 9f);
            Controls.Add(tabs);

            // ---- Tab 1: AI 查询 ----
            TabPage pageAI = new TabPage("AI 查询");
            pageAI.Padding = new Padding(14, 12, 14, 12);
            tabs.TabPages.Add(pageAI);

            chkEnableAI = new CheckBox();
            chkEnableAI.Text = "启用 AI 查询（本地词典查不到时使用）";
            chkEnableAI.Location = new Point(0, 0);
            chkEnableAI.AutoSize = true;

            Label labUrl = new Label(); labUrl.Text = "接口地址 (Base URL)"; labUrl.Location = new Point(0, 40); labUrl.AutoSize = true;
            txtBaseUrl = new TextBox(); txtBaseUrl.Location = new Point(0, 62); txtBaseUrl.Width = 520;

            Label labKey = new Label(); labKey.Text = "API Key"; labKey.Location = new Point(0, 98); labKey.AutoSize = true;
            txtApiKey = new TextBox(); txtApiKey.Location = new Point(0, 120); txtApiKey.Width = 520; txtApiKey.PasswordChar = '*';
            CheckBox chkShowKey = new CheckBox(); chkShowKey.Text = "显示"; chkShowKey.AutoSize = true; chkShowKey.Location = new Point(480, 122);
            chkShowKey.CheckedChanged += delegate { txtApiKey.PasswordChar = chkShowKey.Checked ? '\0' : '*'; };

            Label labModel = new Label(); labModel.Text = "模型名称"; labModel.Location = new Point(0, 156); labModel.AutoSize = true;
            txtModel = new TextBox(); txtModel.Location = new Point(0, 178); txtModel.Width = 280;

            Label labTimeout = new Label(); labTimeout.Text = "超时（秒）"; labTimeout.Location = new Point(320, 156); labTimeout.AutoSize = true;
            numTimeout = new NumericUpDown(); numTimeout.Location = new Point(420, 154); numTimeout.Width = 100; numTimeout.Minimum = 3; numTimeout.Maximum = 120; numTimeout.Value = 20;

            Label hintAI = new Label();
            hintAI.Text = "接口地址支持三种填法（自动补全 /chat/completions）：\n  1. 完整：https://xxx/v1/chat/completions\n  2. https://xxx/v1\n  3. https://xxx\n兼容 OpenAI 格式的服务、中转站、本地部署均可。模型名填服务端支持的。";
            hintAI.Location = new Point(0, 220);
            hintAI.ForeColor = Color.Gray;
            hintAI.AutoSize = true;

            Label labCache = new Label(); labCache.Text = "查询缓存（省 Token）"; labCache.Location = new Point(0, 260); labCache.AutoSize = true;
            chkCache = new CheckBox();
            chkCache.Text = "启用缓存，查过的词条在有效期内直接复用";
            chkCache.Location = new Point(0, 284);
            chkCache.AutoSize = true;
            Label labCacheHours = new Label(); labCacheHours.Text = "缓存有效期（小时）"; labCacheHours.Location = new Point(0, 316); labCacheHours.AutoSize = true;
            numCacheHours = new NumericUpDown(); numCacheHours.Location = new Point(150, 314); numCacheHours.Width = 100; numCacheHours.Minimum = 1; numCacheHours.Maximum = 720; numCacheHours.Value = 24;
            Label hintCache = new Label();
            hintCache.Text = "查询顺序：本地词典 → 缓存 → AI。\n缓存命中时来源栏显示 CACHE，不消耗 Token。";
            hintCache.Location = new Point(0, 346);
            hintCache.ForeColor = Color.Gray;
            hintCache.AutoSize = true;

            pageAI.Controls.AddRange(new Control[] { chkEnableAI, labUrl, txtBaseUrl, labKey, txtApiKey, chkShowKey, labModel, txtModel, labTimeout, numTimeout, hintAI, labCache, chkCache, labCacheHours, numCacheHours, hintCache });

            // ---- Tab 2: 本地词典 ----
            TabPage pageDict = new TabPage("本地词典");
            pageDict.Padding = new Padding(10, 10, 10, 10);
            tabs.TabPages.Add(pageDict);

            dgv = new DataGridView();
            dgv.Dock = DockStyle.Fill;
            dgv.AllowUserToAddRows = false;
            dgv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgv.BackgroundColor = Color.White;
            dgv.RowHeadersVisible = false;
            DataGridViewTextBoxColumn colTerm = new DataGridViewTextBoxColumn();
            colTerm.HeaderText = "词条";
            colTerm.FillWeight = 30;
            DataGridViewTextBoxColumn colDef = new DataGridViewTextBoxColumn();
            colDef.HeaderText = "解释";
            colDef.FillWeight = 70;
            dgv.Columns.AddRange(new DataGridViewColumn[] { colTerm, colDef });

            Panel btnPanel = new Panel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = 44;
            Button btnAdd = new Button(); btnAdd.Text = "添加词条"; btnAdd.Location = new Point(0, 8); btnAdd.Size = new Size(90, 28);
            btnAdd.Click += delegate { dgv.Rows.Add("", ""); dgv.CurrentCell = dgv.Rows[dgv.Rows.Count - 1].Cells[0]; };
            Button btnDel = new Button(); btnDel.Text = "删除选中"; btnDel.Location = new Point(100, 8); btnDel.Size = new Size(90, 28);
            btnDel.Click += delegate { if (dgv.CurrentRow != null && !dgv.CurrentRow.IsNewRow) dgv.Rows.Remove(dgv.CurrentRow); };
            btnPanel.Controls.Add(btnAdd);
            btnPanel.Controls.Add(btnDel);

            Label hintDict = new Label();
            hintDict.Text = "词条优先于 AI 查询命中：零延迟、不花钱。建议把常用术语都放这里。";
            hintDict.Dock = DockStyle.Bottom;
            hintDict.ForeColor = Color.Gray;
            hintDict.Height = 24;

            pageDict.Controls.Add(dgv);
            pageDict.Controls.Add(btnPanel);
            pageDict.Controls.Add(hintDict);

            // ---- Tab 3: 常规 ----
            TabPage pageCommon = new TabPage("常规");
            pageCommon.Padding = new Padding(14, 12, 14, 12);
            tabs.TabPages.Add(pageCommon);

            Label labBubble = new Label(); labBubble.Text = "气泡自动关闭（秒）"; labBubble.Location = new Point(0, 0); labBubble.AutoSize = true;
            numBubbleSec = new NumericUpDown(); numBubbleSec.Location = new Point(230, 0); numBubbleSec.Width = 100; numBubbleSec.Minimum = 3; numBubbleSec.Maximum = 120;

            Label labCooldown = new Label(); labCooldown.Text = "同词重复查询间隔（秒）"; labCooldown.Location = new Point(0, 44); labCooldown.AutoSize = true;
            numCooldown = new NumericUpDown(); numCooldown.Location = new Point(230, 44); numCooldown.Width = 100; numCooldown.Minimum = 0; numCooldown.Maximum = 120;

            chkStartup = new CheckBox();
            chkStartup.Text = "开机自动启动";
            chkStartup.Location = new Point(0, 88);
            chkStartup.AutoSize = true;

            Label labHotkey = new Label(); labHotkey.Text = "查询热键"; labHotkey.Location = new Point(0, 136); labHotkey.AutoSize = true;
            hotkeyBox = new TextBox();
            hotkeyBox.Location = new Point(110, 132);
            hotkeyBox.Width = 200;
            hotkeyBox.ReadOnly = true;
            hotkeyBox.KeyDown += hotkeyBox_KeyDown;
            btnHotkey = new Button();
            btnHotkey.Text = "修改";
            btnHotkey.Location = new Point(322, 130);
            btnHotkey.Size = new Size(60, 26);
            btnHotkey.Click += delegate { ToggleCapture(); };

            Label hintCommon = new Label();
            hintCommon.Text = "使用方式：\n  选中文字后按查询热键即可查询。\n  点\"修改\"后按下新的组合键可自定义热键。\n气泡出现在鼠标旁，点击或等待自动关闭。";
            hintCommon.Location = new Point(0, 174);
            hintCommon.ForeColor = Color.Gray;
            hintCommon.AutoSize = true;

            pageCommon.Controls.AddRange(new Control[] { labBubble, numBubbleSec, labCooldown, numCooldown, chkStartup, labHotkey, hotkeyBox, btnHotkey, hintCommon });

            // ---- 底部按钮 ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;
            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = new Point(392, 10);
            btnSave.Size = new Size(80, 30);
            btnSave.Click += delegate { Save(); };
            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(484, 10);
            btnCancel.Size = new Size(80, 30);
            btnCancel.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(btnSave);
            bottom.Controls.Add(btnCancel);
            Controls.Add(bottom);

            LoadValues();
        }

        private void LoadValues()
        {
            chkEnableAI.Checked = settings.EnableAI;
            chkCache.Checked = settings.EnableCache;
            if (settings.CacheHours < 1) settings.CacheHours = 24;
            if (settings.CacheHours > 720) settings.CacheHours = 720;
            numCacheHours.Value = settings.CacheHours;
            txtBaseUrl.Text = settings.BaseUrl;
            txtApiKey.Text = settings.ApiKey;
            txtModel.Text = settings.Model;
            if (settings.TimeoutSec < 3) settings.TimeoutSec = 20;
            if (settings.TimeoutSec > 120) settings.TimeoutSec = 120;
            numTimeout.Value = settings.TimeoutSec;
            if (settings.BubbleSeconds < 3) settings.BubbleSeconds = 10;
            if (settings.BubbleSeconds > 120) settings.BubbleSeconds = 120;
            numBubbleSec.Value = settings.BubbleSeconds;
            if (settings.QueryCooldownSec < 0) settings.QueryCooldownSec = 3;
            if (settings.QueryCooldownSec > 120) settings.QueryCooldownSec = 120;
            numCooldown.Value = settings.QueryCooldownSec;

            curMods = settings.QueryModifiers;
            curVk = settings.QueryVk;
            hotkeyBox.Text = Settings.HotkeyName(curMods, curVk);

            foreach (KeyValuePair<string, string> kv in settings.Dict)
            {
                dgv.Rows.Add(kv.Key, kv.Value);
            }

            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key != null && key.GetValue("划词查询") != null) chkStartup.Checked = true;
                }
            }
            catch { }
        }

        private void Save()
        {
            settings.EnableAI = chkEnableAI.Checked;
            settings.EnableCache = chkCache.Checked;
            settings.CacheHours = (int)numCacheHours.Value;
            settings.BaseUrl = txtBaseUrl.Text.Trim();
            settings.ApiKey = txtApiKey.Text.Trim();
            settings.Model = txtModel.Text.Trim();
            settings.TimeoutSec = (int)numTimeout.Value;
            settings.BubbleSeconds = (int)numBubbleSec.Value;
            settings.QueryCooldownSec = (int)numCooldown.Value;
            settings.QueryModifiers = curMods;
            settings.QueryVk = curVk;

            settings.Dict.Clear();
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;
                string k = (row.Cells[0].Value == null ? "" : row.Cells[0].Value.ToString()).Trim();
                string v = (row.Cells[1].Value == null ? "" : row.Cells[1].Value.ToString()).Trim();
                if (k.Length > 0) settings.Dict[k] = v;
            }

            try
            {
                settings.Save(cfgPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "划词查询", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (chkStartup.Checked) key.SetValue("划词查询", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("划词查询", false);
                }
            }
            catch { }

            DialogResult = DialogResult.OK;
            Close();
        }

        // ===== 热键自定义 =====
        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void ToggleCapture()
        {
            if (!capturingHotkey)
            {
                capturingHotkey = true;
                hotkeyBox.ReadOnly = false;
                hotkeyBox.Text = "请按下新的组合键…（Esc 取消）";
                hotkeyBox.Focus();
            }
            else
            {
                capturingHotkey = false;
                hotkeyBox.ReadOnly = true;
                hotkeyBox.Text = Settings.HotkeyName(curMods, curVk);
            }
        }

        private void hotkeyBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!capturingHotkey) return;
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Escape) { ToggleCapture(); return; }
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;

            int mods = 0;
            if ((e.Modifiers & Keys.Control) != 0) mods |= 0x0002;
            if ((e.Modifiers & Keys.Alt) != 0) mods |= 0x0001;
            if ((e.Modifiers & Keys.Shift) != 0) mods |= 0x0004;
            if (mods == 0)
            {
                MessageBox.Show("请配合 Ctrl / Alt / Shift 使用，至少一个修饰键。", "划词查询", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int vk = (int)e.KeyCode;
            if (TestHotkey(mods, vk))
            {
                curMods = mods;
                curVk = vk;
                capturingHotkey = false;
                hotkeyBox.ReadOnly = true;
                hotkeyBox.Text = Settings.HotkeyName(curMods, curVk);
            }
            else
            {
                MessageBox.Show("该组合键已被占用或不可用，请换一个。", "划词查询", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                hotkeyBox.Text = "请按下新的组合键…";
            }
        }

        // 试注册检测冲突（不常驻）
        private bool TestHotkey(int mods, int vk)
        {
            try
            {
                bool ok = RegisterHotKey(IntPtr.Zero, 9999, mods | 0x4000, vk);
                if (ok) UnregisterHotKey(IntPtr.Zero, 9999);
                return ok;
            }
            catch { return false; }
        }
    }
}
