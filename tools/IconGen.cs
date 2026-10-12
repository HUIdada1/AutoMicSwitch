// IconGen.cs — 构建期小工具
//
// 把托盘图标那套画法按几个尺寸各渲染一遍，拼成 .ico 给 exe 当图标，
// 免得装完之后快捷方式是张白纸。只在编译时跑一次，不参与主程序。
//
// 用法：IconGen.exe <输出路径.ico>
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly int[] Sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";

        List<byte[]> frames = new List<byte[]>();
        foreach (int s in Sizes) frames.Add(Render(s));

        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        WriteIco(outPath, frames);
        Console.WriteLine("icon written: " + outPath);
    }

    // 画法跟 Program.cs 里的托盘图标保持一致，这里只是换个尺寸重画
    static byte[] Render(int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);

                using (GraphicsPath path = RoundedRect(new RectangleF(1, 1, 30, 30), 8))
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new RectangleF(0, 0, 32, 32), Color.FromArgb(56, 189, 248), Color.FromArgb(99, 102, 241), 45f))
                    g.FillPath(b, path);

                using (SolidBrush w = new SolidBrush(Color.FromArgb(235, 240, 255)))
                {
                    using (GraphicsPath cap = RoundedRect(new RectangleF(12, 6, 8, 14), 4))
                        g.FillPath(w, cap);
                    g.FillRectangle(w, 14, 20, 4, 4);
                    using (Pen p = new Pen(Color.FromArgb(235, 240, 255), 2.2f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawArc(p, 9, 12, 14, 12, 0, 180);
                        g.DrawLine(p, 16, 24, 16, 27);
                        g.DrawLine(p, 12, 27, 20, 27);
                    }
                }
            }

            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 每个尺寸塞一张 PNG（Vista 以后都认），省得自己拼 DIB 和掩码
    static void WriteIco(string path, List<byte[]> frames)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)Sizes.Length);

            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                w.Write(dim);
                w.Write(dim);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write((uint)frames[i].Length);
                w.Write((uint)offset);
                offset += frames[i].Length;
            }

            for (int i = 0; i < frames.Count; i++) w.Write(frames[i]);
        }
    }
}
