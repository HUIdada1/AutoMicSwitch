// PackRelease.cs — 构建期小工具
//
// 生成 GitHub Release 用的 latest.yml：版本、大小、sha512，外加上一段更新说明。
// 客户端就靠这个文件比对版本，下载完再用 sha512 核一遍。
//
// 用法：PackRelease.exe <exe路径> <输出latest.yml> [更新说明.md]
//
// 作者：沐辉 · MIT License
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static class PackRelease
{
    static void Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : @"dist\AutoMicSwitch.exe";
        string outYml = args.Length > 1 ? args[1] : @"dist\latest.yml";
        string notesFile = args.Length > 2 ? args[2] : "";

        if (!File.Exists(exe))
        {
            Console.Error.WriteLine("找不到 " + exe + "，先跑 build.cmd");
            Environment.Exit(1);
        }

        string version = FileVersionInfo.GetVersionInfo(Path.GetFullPath(exe)).FileVersion;
        string[] parts = (version ?? "").Split('.');
        if (parts.Length >= 3) version = parts[0] + "." + parts[1] + "." + parts[2];

        string sha;
        using (FileStream fs = File.OpenRead(exe))
        using (SHA512 alg = SHA512.Create())
            sha = Convert.ToBase64String(alg.ComputeHash(fs));

        long size = new FileInfo(exe).Length;

        string notes = "";
        if (notesFile.Length > 0 && File.Exists(notesFile))
            notes = File.ReadAllText(notesFile, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd();

        StringBuilder sb = new StringBuilder();
        sb.Append("version: ").Append(version).Append('\n');
        sb.Append("sha512: ").Append(sha).Append('\n');
        sb.Append("size: ").Append(size).Append('\n');
        sb.Append("releaseNotes: |-\n");
        foreach (string line in notes.Split('\n'))
            sb.Append("  ").Append(line).Append('\n');

        string dir = Path.GetDirectoryName(Path.GetFullPath(outYml));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outYml, sb.ToString(), new UTF8Encoding(false));

        Console.WriteLine("latest.yml 已生成：" + version + " / " + size + " 字节");
    }
}
