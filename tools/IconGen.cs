// IconGen.cs — 构建期小工具
//
// 把 AppIcon 那套画法按几个尺寸各渲染一遍，拼成 .ico 给 exe 当图标，
// 免得装完之后快捷方式是张白纸。只在编译时跑一次，不参与主程序。
//
// 画法本身在 src\AppIcon.cs 里，跟托盘图标、窗口图标共用一份，
// 改一处三处一起变，不会出现「快捷方式图标和托盘图标长得不一样」。
//
// 用法：IconGen.exe <输出路径.ico>
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AutoMicSwitch;

static class IconGen
{
    static readonly int[] Sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";

        List<byte[]> frames = new List<byte[]>();
        foreach (int s in Sizes) frames.Add(Png(s));

        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        WriteIco(outPath, frames);
        Console.WriteLine("icon written: " + outPath);
    }

    static byte[] Png(int size)
    {
        using (Bitmap bmp = AppIcon.Render(size))
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
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
