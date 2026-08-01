// IcoGen: Glosser 图标生成器（多尺寸 DIB 打包 ICO，兼容 csc 资源编译器）
// 用法: IcoGen.exe <输出.ico>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public class IcoGen
{
    static void Main(string[] args)
    {
        string outPath = args[0];
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> datas = new List<byte[]>();
        List<int> whs = new List<int>();

        foreach (int s in sizes)
        {
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.ScaleTransform((float)s / 256f, (float)s / 256f);
                Draw(g);
            }
            byte[] dib = ToDIB(bmp);
            bmp.Dispose();
            whs.Add(s == 256 ? 0 : s);
            datas.Add(dib);
        }

        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);
            int cur = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)whs[i]);
                bw.Write((byte)whs[i]);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)datas[i].Length);
                bw.Write((uint)cur);
                cur += datas[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(datas[i]);
            }
        }
        Console.WriteLine("ICO written: " + outPath);
    }

    static void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Rounded(0, 0, 256, 256, 56))
        {
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 8, 8, 8)))
                g.FillPath(bg, path);
            using (Pen pen = new Pen(Color.FromArgb(255, 42, 42, 42), 2f))
                g.DrawPath(pen, path);
        }
        // 轨道
        using (Pen orbit = new Pen(Color.FromArgb(255, 165, 165, 165), 2.5f))
            g.DrawEllipse(orbit, 132, 2, 96, 30);
        // 卫星
        using (SolidBrush sat = new SolidBrush(Color.FromArgb(255, 240, 240, 240)))
            g.FillEllipse(sat, 200, 4, 13, 13);
        // Q
        using (Font font = new Font("Segoe UI", 158f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush white = new SolidBrush(Color.White))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            g.DrawString("Q", font, white, new RectangleF(0, 26, 256, 186), sf);
        }
        // 下划线
        using (Pen line = new Pen(Color.FromArgb(255, 190, 190, 190), 3f))
            g.DrawLine(line, 82, 212, 174, 212);
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        GraphicsPath path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static byte[] ToDIB(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte[] bytes = new byte[w * h * 4];
        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);

        int andStride = ((w + 31) / 32) * 4;
        byte[] dib = new byte[40 + w * h * 4 + andStride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h * 2).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(0).CopyTo(dib, 16);
        BitConverter.GetBytes(w * h * 4).CopyTo(dib, 20);
        for (int row = 0; row < h; row++)
        {
            int src = (h - 1 - row) * w * 4;
            int dst = 40 + row * w * 4;
            Array.Copy(bytes, src, dib, dst, w * 4);
        }
        return dib;
    }
}
