# Glosser · 划词查询

> Windows 全局划词查询工具：选中任意文字，按下快捷键，鼠标旁弹出解释卡片。

一个常驻系统托盘的 Windows 小工具。在任何软件里选中文字，按一下查询热键，鼠标旁就会出现一张深色极简风格的词条解释卡片。

## 特性

- **任意软件通用**：浏览器、记事本、PDF 阅读器、Office、聊天窗口……
- **三级查询链**：本地词典（秒回·免费）→ 查询缓存（不花 Token）→ AI 大模型（兜底）
- **查询结果持久化缓存**：同词条在有效期内直接复用，不重复消耗 Token
- **深色极简太空风卡片**：大写标签排版、细线分隔、淡入动画
- **单文件 exe，零依赖**：仅用 Windows 自带的 .NET Framework，无需安装任何运行时
- **全中文界面**，设置项图形化，热键可自定义

## 快速开始

1. 从 Releases 下载 `划词查询.exe`，双击运行
2. 首次运行自动弹出设置窗，填入 OpenAI 兼容接口的 API Key
   （默认地址为 `https://api.openai.com/v1/chat/completions`，中转站或本地部署亦可，路径自动补全）
3. 在任意软件里选中文字，按 `Ctrl+Alt+Q`

## 使用说明

- **查询**：选中文字 → 按查询热键（默认 `Ctrl+Alt+Q`）→ 气泡出现在鼠标旁
- **热键**：设置 → 常规 → 查询热键 → 修改，按下新组合键即可，自动检测占用
- **本地词典**：设置 → 本地词典，表格化管理词条，命中时秒回且免费
- **缓存**：设置 → AI 查询 → 查询缓存，有效期可调（默认 24 小时）

## 隐私说明

- 本软件**不收集任何统计数据**，不含遥测，不向任何服务器上报信息
- 配置（settings.json）与查询缓存（cache.json）仅保存在本地
- 唯一的外部请求：当你查询且本地词典/缓存均未命中时，向你在设置中配置的 AI 接口发起一次查询
- 使用 AI 接口产生的费用与条款，由你与接口提供方之间的协议决定

## 工作原理

```
选中文字 → 按热键 → keybd_event 模拟 Ctrl+C 取词
                 → 剪贴板序列号检测复制是否生效
                 → 本地词典 → cache.json 缓存 → OpenAI 兼容接口
                 → 结果写回缓存 → 气泡展示
```

1. `RegisterHotKey` 注册全局热键（默认 Ctrl+Alt+Q，不劫持系统 Ctrl+C）
2. 按下热键后通过 `keybd_event` 模拟 `Ctrl+C` 将选中文本复制进剪贴板
3. 用 `GetClipboardSequenceNumber` 检测复制是否生效；某些高权限/特殊窗口会拒绝模拟按键，此时明确提示，手动复制后重试即可
4. 查询按「本地词典 → 缓存 → AI」顺序执行，AI 结果写入 `cache.json` 持久化

## 构建

依赖：Windows 10/11（自带 .NET Framework 4.x），无需任何额外安装。

```bat
build.bat
```

或手动编译：

```bat
csc /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:glosser.ico /out:划词查询.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll 划词查询.cs
```

## 文件结构

```
划词查询.exe       编译好的程序（直接运行）
划词查询.cs        全部源码
build.bat          一键编译脚本
glosser.ico        程序图标
IcoGen.cs          图标生成工具源码
使用说明.txt       中文使用文档
```

运行时会在 exe 同目录生成：`settings.json`（配置）、`cache.json`（查询缓存）、`debug.log`（运行日志）。

## 常见问题

**Q: 提示「未检测到划词复制」？**
当前窗口（如某些高权限程序）拒绝了模拟按键。手动 `Ctrl+C` 复制后再按查询热键即可，功能不受影响。

**Q: 热键被其他程序占用？**
设置 → 常规 → 查询热键 → 修改，换一个未被占用的组合键。

**Q: 会消耗多少 Token？**
命中本地词典或缓存时零消耗；只有 AI 兜底查询才计费，且结果会写入缓存供后续复用。

## License

[MIT](LICENSE)
