// Setup.cs — 安装 / 卸载
//
// 单文件安装器：主程序和它的运行库作为资源嵌进来，装到当前用户目录，不碰系统目录，
// 全程不需要管理员权限。卸载信息写在 HKCU，跟安装位置一个层级。
//
// 装之前先做环境预检（系统架构 / .NET Framework / WebView2 运行时），
// 缺什么当场说清楚，别等装完打不开界面再让用户猜。
//
// 用法：
//   AutoMicSwitch-Setup.exe               图形界面安装
//   AutoMicSwitch-Setup.exe /S            静默安装（装完不启动）
//   AutoMicSwitch-Setup.exe /S /relaunch  静默安装并启动，更新走这条
//   AutoMicSwitch-Setup.exe /uninstall    图形界面卸载
//   AutoMicSwitch-Setup.exe /uninstall /S 静默卸载
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoMicSwitchSetup
{
    internal static class Setup
    {
        const string AppName = "AutoMicSwitch";
        const string RulesName = "payload_rules.example.txt";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
        const string PrefKey = @"Software\AutoMicSwitch";

        // 界面跑在 WebView2 里，运行时装不上就没界面可看，装之前先问一声
        const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        public const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // .NET Framework 4.6.2 对应的 Release 号，WebView2 的托管库要求不低于它
        const int NetFxMinRelease = 394802;

        // ---------- 载荷清单 ----------
        // 资源名（嵌在安装包里的名字）→ 装完之后在安装目录里的相对路径。
        // 三套 WebView2Loader 都得带上：主程序是 AnyCPU，同一个 exe 在不同机器上
        // 进程位数不一样，SDK 会按当前进程架构去 runtimes\win-<arch>\native 里挑。
        static readonly string[][] Payload = new string[][]
        {
            new string[] { "payload_AutoMicSwitch.exe",                   AppName + ".exe" },
            new string[] { "payload_Microsoft.Web.WebView2.Core.dll",     "Microsoft.Web.WebView2.Core.dll" },
            new string[] { "payload_Microsoft.Web.WebView2.WinForms.dll", "Microsoft.Web.WebView2.WinForms.dll" },
            new string[] { "payload_WebView2Loader.x86.dll",              @"runtimes\win-x86\native\WebView2Loader.dll" },
            new string[] { "payload_WebView2Loader.x64.dll",              @"runtimes\win-x64\native\WebView2Loader.dll" },
            new string[] { "payload_WebView2Loader.arm64.dll",            @"runtimes\win-arm64\native\WebView2Loader.dll" },
        };

        internal static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName); }
        }
        internal static string AppExe { get { return Path.Combine(InstallDir, AppName + ".exe"); } }
        internal static string UninstExe { get { return Path.Combine(InstallDir, "Uninstall.exe"); } }
        internal static string RulesFile { get { return Path.Combine(InstallDir, "rules.txt"); } }
        internal static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }
        internal static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool silent = false, uninstall = false, relaunch = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "/s" || s == "-s" || s == "/silent") silent = true;
                if (s == "/uninstall" || s == "-u" || s == "/remove") uninstall = true;
                if (s == "/relaunch") relaunch = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uninstall)
            {
                if (silent) { DoUninstall(null); return; }
                using (UninstallForm f = new UninstallForm()) { f.ShowDialog(); }
                return;
            }

            if (silent)
            {
                // 开机自启默认开着，快捷方式照旧建，启动与否看 /relaunch
                try { DoInstall(true, true, relaunch, null); }
                catch { }
                return;
            }

            using (InstallForm f = new InstallForm()) { f.ShowDialog(); }
        }

        // ---------- 环境预检 ----------
        internal class EnvItem
        {
            public string Name;
            public bool Ok;
            public string Detail;
            public bool Link;      // Detail 是可点的下载入口
        }

        internal static List<EnvItem> CheckEnvironment()
        {
            List<EnvItem> list = new List<EnvItem>();

            list.Add(new EnvItem { Name = "系统架构", Ok = true, Detail = OsArch() });

            int rel = NetFxRelease();
            if (rel >= NetFxMinRelease)
            {
                list.Add(new EnvItem { Name = ".NET Framework", Ok = true, Detail = NetFxName(rel) });
            }
            else
            {
                list.Add(new EnvItem
                {
                    Name = ".NET Framework",
                    Ok = false,
                    Detail = rel <= 0 ? "未检测到 4.x，程序无法运行" : "版本过低（" + NetFxName(rel) + "），需要 4.6.2 或更高"
                });
            }

            string wv = WebView2Version();
            if (wv != null)
            {
                list.Add(new EnvItem { Name = "WebView2 运行时", Ok = true, Detail = wv });
            }
            else
            {
                list.Add(new EnvItem
                {
                    Name = "WebView2 运行时",
                    Ok = false,
                    Detail = "未安装，点这里去装（界面需要它）",
                    Link = true
                });
            }

            return list;
        }

        internal static string OsArch()
        {
            string os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (string.IsNullOrEmpty(os)) os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            os = (os == null ? "" : os).ToUpperInvariant();
            if (os.IndexOf("ARM64", StringComparison.Ordinal) >= 0) return "ARM64";
            if (os.IndexOf("AMD64", StringComparison.Ordinal) >= 0) return "x64";
            if (os.IndexOf("X86", StringComparison.Ordinal) >= 0) return "x86";
            return "未知";
        }

        static int NetFxRelease()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", false))
                {
                    if (k == null) return 0;
                    object v = k.GetValue("Release");
                    if (v == null) return 0;
                    return Convert.ToInt32(v);
                }
            }
            catch { return 0; }
        }

        static string NetFxName(int release)
        {
            if (release >= 533320) return "4.8.1 或更高";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release > 0) return "4.x（Release " + release + "）";
            return "未安装";
        }

        // 系统级和用户级都可能装一份，都看一遍
        internal static string WebView2Version()
        {
            string[] machinePaths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId,
            };
            foreach (string p in machinePaths)
            {
                string v = ReadPv(Registry.LocalMachine, p);
                if (v != null) return v;
            }
            return ReadPv(Registry.CurrentUser, @"Software\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId);
        }

        static string ReadPv(RegistryKey root, string path)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue("pv");
                    string s = v == null ? null : Convert.ToString(v);
                    return string.IsNullOrEmpty(s) ? null : s;
                }
            }
            catch { return null; }
        }

        // ---------- 安装 ----------
        internal static bool DoInstall(bool autostart, bool desktop, bool launch, Action<string> log)
        {
            try
            {
                Step(log, "校验安装包");
                VerifyPayload();

                Step(log, "关闭正在运行的程序");
                KillRunning();

                Step(log, "写入程序文件");
                ExtractPayload();

                CopySelf(UninstExe);

                // 只在没有的时候放一份，升级不覆盖用户改过的规则
                if (!File.Exists(RulesFile))
                {
                    try { WriteResource(RulesName, RulesFile); } catch { }
                }

                Step(log, "创建快捷方式");
                CreateShortcut(StartMenuLink, AppExe, "", InstallDir);
                if (desktop) CreateShortcut(DesktopLink, AppExe, "", InstallDir);
                else TryDelete(DesktopLink);

                Step(log, "登记卸载信息");
                WriteUninstallEntry();

                Step(log, autostart ? "设置开机自启" : "关闭开机自启");
                SetAutostart(autostart);

                if (launch)
                {
                    Step(log, "启动程序");
                    Process.Start(new ProcessStartInfo(AppExe) { WorkingDirectory = InstallDir });
                }

                Step(log, "完成");
                return true;
            }
            catch (Exception e)
            {
                Step(log, "安装失败：" + e.Message);
                return false;
            }
        }

        // 缺一个文件就是半个安装包，宁可在动手之前就拦下来，
        // 也不要把旧版本拆了一半再报错
        static void VerifyPayload()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            List<string> missing = new List<string>();
            foreach (string[] item in Payload)
            {
                if (asm.GetManifestResourceStream(item[0]) == null) missing.Add(item[0]);
            }
            if (missing.Count > 0)
            {
                throw new Exception("安装包不完整，缺少 " + string.Join("、", missing.ToArray()) + "，请重新下载");
            }
        }

        static void ExtractPayload()
        {
            Directory.CreateDirectory(InstallDir);
            foreach (string[] item in Payload)
            {
                string dest = Path.Combine(InstallDir, item[1]);
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteResource(item[0], dest);
            }
        }

        // ---------- 卸载 ----------
        internal static bool DoUninstall(Action<string> log)
        {
            try
            {
                // 卸载程序往往就是从安装目录里启动的，CWD 卡在里面会让删目录失败，
                // 先把自己挪到临时目录去
                try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }

                Step(log, "关闭正在运行的程序");
                KillRunning();

                Step(log, "移除开机自启");
                SetAutostart(false);

                Step(log, "删除快捷方式");
                TryDelete(StartMenuLink);
                TryDelete(DesktopLink);

                Step(log, "清除注册表项");
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(PrefKey, false); } catch { }

                Step(log, "删除程序文件");
                if (IsUnder(Application.ExecutablePath, InstallDir))
                {
                    // 卸载程序自己就在这个目录里，它自己是删不掉的：
                    // 先把别的文件清掉，剩下的目录交给脚本，等本进程退出再收尾
                    foreach (string f in SafeFiles(InstallDir))
                    {
                        if (!SamePath(f, Application.ExecutablePath)) TryDelete(f);
                    }
                    LaunchCleanup(InstallDir);
                }
                else
                {
                    // 从外面跑的（比如 dist 里的安装包），直接删干净
                    TryDeleteDir(InstallDir);
                }

                Step(log, "完成");
                return true;
            }
            catch (Exception e)
            {
                Step(log, "卸载失败：" + e.Message);
                return false;
            }
        }

        static void Step(Action<string> log, string text)
        {
            if (log != null) log(text);
        }

        // ---------- 具体动作 ----------
        // 杀进程后文件句柄不一定立刻释放，这里多等一会儿
        static void KillRunning()
        {
            for (int round = 0; round < 20; round++)
            {
                Process[] list = Process.GetProcessesByName(AppName);
                if (list.Length == 0) break;
                foreach (Process p in list)
                {
                    try { p.Kill(); p.WaitForExit(3000); }
                    catch { }
                }
                Thread.Sleep(300);
            }
            Thread.Sleep(500);
        }

        static void WriteResource(string name, string path)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("安装包不完整，找不到内置的 " + name);
                using (FileStream fs = OpenForWrite(path))
                    s.CopyTo(fs);
            }
        }

        // 目标文件可能还被旧进程占着，重试几次再放弃
        static FileStream OpenForWrite(string path)
        {
            Exception last = null;
            for (int i = 0; i < 20; i++)
            {
                try { return new FileStream(path, FileMode.Create, FileAccess.Write); }
                catch (Exception e) { last = e; Thread.Sleep(300); }
            }
            throw new Exception("写入 " + Path.GetFileName(path) + " 失败：" + last.Message);
        }

        // 安装器自己也复制一份进去，卸载入口指着它，用户删掉下载的安装包也不影响卸载
        static void CopySelf(string path)
        {
            string self = Application.ExecutablePath;
            if (SamePath(self, path)) return;
            for (int i = 0; i < 10; i++)
            {
                try { File.Copy(self, path, true); return; }
                catch { Thread.Sleep(300); }
            }
            throw new Exception("复制卸载程序失败");
        }

        static void WriteUninstallEntry()
        {
            string version = ReadVersion(AppExe);
            long size = 0;
            try
            {
                foreach (string f in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories))
                    size += new FileInfo(f).Length;
            }
            catch { }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "AutoMicSwitch");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "沐辉");
                k.SetValue("DisplayIcon", AppExe + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstExe + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstExe + "\" /uninstall /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }
        }

        internal static string ReadVersion(string exePath)
        {
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
            }
            catch { }
            return "1.0.0";
        }

        internal static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(AppName, "\"" + AppExe + "\" --startup", RegistryValueKind.String);
                    else if (k.GetValue(AppName) != null) k.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        internal static bool IsInstalled()
        {
            return File.Exists(AppExe);
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        static string[] SafeFiles(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : new string[0]; }
            catch { return new string[0]; }
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool IsUnder(string file, string dir)
        {
            try
            {
                string f = Path.GetFullPath(file);
                string d = Path.GetFullPath(dir);
                if (!d.EndsWith("\\")) d += "\\";
                return f.StartsWith(d, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 收尾脚本：等本进程退出、句柄释放，再删目录。最多重试 30 次，免得卡死。
        // 开头必须切走工作目录——卸载程序多半是从安装目录里被拉起来的，
        // 脚本继承了那个 CWD 的话，rd 会以「目录正被占用」失败
        static void LaunchCleanup(string dir)
        {
            string bat = Path.Combine(Path.GetTempPath(), "AutoMicSwitch-cleanup.cmd");
            string script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "cd /d \"%TEMP%\"\r\n" +
                "set \"DIR=" + dir + "\"\r\n" +
                "set /a N=0\r\n" +
                ":loop\r\n" +
                "if not exist \"%DIR%\" goto done\r\n" +
                "rd /s /q \"%DIR%\" >nul 2>&1\r\n" +
                "if not exist \"%DIR%\" goto done\r\n" +
                "set /a N+=1\r\n" +
                "if %N% GEQ 30 goto done\r\n" +
                "ping -n 2 127.0.0.1 >nul\r\n" +
                "goto loop\r\n" +
                ":done\r\n" +
                "del /f /q \"%~f0\" >nul 2>&1\r\n";
            File.WriteAllText(bat, script, Encoding.Default);

            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
        }

        // 用 WScript.Shell 建 .lnk，省得自己拼二进制格式
        static void CreateShortcut(string lnk, string target, string args, string workDir)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new Exception("系统缺少 WScript.Shell，无法创建快捷方式");

            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { args });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "默认播放设备变化时，自动把默认麦克风切到对应设备" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    // ---------- 安装界面 ----------
    internal class InstallForm : Form
    {
        static readonly Color Ink = Color.FromArgb(22, 28, 38);
        static readonly Color Sub = Color.FromArgb(108, 120, 136);
        static readonly Color Faint = Color.FromArgb(150, 160, 174);
        static readonly Color Line = Color.FromArgb(228, 232, 239);
        static readonly Color Accent = Color.FromArgb(30, 118, 172);
        static readonly Color OkInk = Color.FromArgb(18, 133, 92);
        static readonly Color WarnInk = Color.FromArgb(169, 114, 18);

        readonly CheckBox cbAuto = new CheckBox();
        readonly CheckBox cbDesktop = new CheckBox();
        readonly CheckBox cbLaunch = new CheckBox();
        readonly Label status = new Label();
        readonly Button btnGo = new Button();
        readonly Button btnCancel = new Button();
        bool working;

        public InstallForm()
        {
            bool installed = Setup.IsInstalled();

            Text = installed ? "AutoMicSwitch 更新" : "AutoMicSwitch 安装";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(524, 412);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 251, 253);
            ForeColor = Ink;

            Label title = new Label();
            title.Text = "AutoMicSwitch";
            title.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(14, 74, 108);
            title.SetBounds(26, 20, 460, 32);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "默认播放设备变化时，自动把默认麦克风切到对应设备";
            sub.ForeColor = Sub;
            sub.SetBounds(28, 54, 470, 20);
            Controls.Add(sub);

            Label path = new Label();
            path.Text = "安装到：" + Setup.InstallDir;
            path.ForeColor = Faint;
            path.SetBounds(28, 78, 470, 20);
            Controls.Add(path);

            Controls.Add(Divider(26, 106));

            Label envHead = new Label();
            envHead.Text = "环境检查";
            envHead.ForeColor = Faint;
            envHead.Font = new Font("Microsoft YaHei UI", 8.5f);
            envHead.SetBounds(28, 114, 200, 18);
            Controls.Add(envHead);

            int y = 136;
            foreach (Setup.EnvItem item in Setup.CheckEnvironment())
            {
                foreach (Control c in EnvRow(item, y)) Controls.Add(c);
                y += 23;
            }

            Controls.Add(Divider(26, 208));

            cbAuto.Text = "开机自动启动";
            cbAuto.Checked = true;
            cbAuto.SetBounds(28, 222, 300, 24);
            Controls.Add(cbAuto);

            cbDesktop.Text = "创建桌面快捷方式";
            cbDesktop.Checked = true;
            cbDesktop.SetBounds(28, 248, 300, 24);
            Controls.Add(cbDesktop);

            cbLaunch.Text = "安装完成后立即运行";
            cbLaunch.Checked = true;
            cbLaunch.SetBounds(28, 274, 300, 24);
            Controls.Add(cbLaunch);

            status.Text = installed ? "已安装过，本次为覆盖更新（rules.txt 会保留）" : "";
            status.ForeColor = Sub;
            status.SetBounds(28, 308, 470, 20);
            Controls.Add(status);

            btnGo.Text = installed ? "更新" : "开始安装";
            btnGo.SetBounds(292, 348, 108, 34);
            btnGo.FlatStyle = FlatStyle.Flat;
            btnGo.BackColor = Accent;
            btnGo.ForeColor = Color.White;
            btnGo.FlatAppearance.BorderSize = 0;
            btnGo.Click += delegate { Go(); };
            Controls.Add(btnGo);

            btnCancel.Text = "取消";
            btnCancel.SetBounds(408, 348, 88, 34);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = Color.FromArgb(236, 240, 245);
            btnCancel.ForeColor = Ink;
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(214, 221, 230);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            AcceptButton = btnGo;
            CancelButton = btnCancel;
        }

        static Panel Divider(int x, int y)
        {
            Panel p = new Panel();
            p.BackColor = Line;
            p.SetBounds(x, y, 472, 1);
            return p;
        }

        static Control[] EnvRow(Setup.EnvItem item, int y)
        {
            Label mark = new Label();
            mark.Text = item.Ok ? "√" : "!";
            mark.ForeColor = item.Ok ? OkInk : WarnInk;
            mark.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            mark.SetBounds(30, y, 18, 20);

            Label name = new Label();
            name.Text = item.Name;
            name.ForeColor = Ink;
            name.SetBounds(52, y, 132, 20);

            if (item.Link)
            {
                LinkLabel link = new LinkLabel();
                link.Text = item.Detail;
                link.LinkColor = WarnInk;
                link.ActiveLinkColor = Accent;
                link.SetBounds(188, y, 310, 20);
                link.LinkClicked += delegate { OpenWebView2Page(); };
                return new Control[] { mark, name, link };
            }

            Label detail = new Label();
            detail.Text = item.Detail;
            detail.ForeColor = item.Ok ? Sub : WarnInk;
            detail.SetBounds(188, y, 310, 20);

            return new Control[] { mark, name, detail };
        }

        static void OpenWebView2Page()
        {
            try { Process.Start(Setup.WebView2DownloadUrl); }
            catch { }
        }

        void Go()
        {
            if (working) return;
            working = true;
            btnGo.Enabled = false;
            btnCancel.Enabled = false;
            cbAuto.Enabled = cbDesktop.Enabled = cbLaunch.Enabled = false;

            bool ok = Setup.DoInstall(cbAuto.Checked, cbDesktop.Checked, cbLaunch.Checked,
                delegate (string s) { status.Text = s + "…"; status.Refresh(); });

            if (ok)
            {
                string extra = Setup.WebView2Version() == null
                    ? "\n\n注意：这台机器还没有 WebView2 运行时，界面需要它。\n"
                      + "首次打开程序时会给出安装入口。"
                    : "";
                MessageBox.Show("安装完成。\n\n程序目录：" + Setup.InstallDir
                    + "\n开机自启：" + (cbAuto.Checked ? "已开启" : "已关闭") + extra,
                    "AutoMicSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            else
            {
                btnGo.Enabled = true;
                btnCancel.Enabled = true;
                cbAuto.Enabled = cbDesktop.Enabled = cbLaunch.Enabled = true;
                working = false;
                MessageBox.Show(status.Text, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ---------- 卸载界面 ----------
    internal class UninstallForm : Form
    {
        readonly Label status = new Label();
        readonly Button btnGo = new Button();
        readonly Button btnCancel = new Button();
        bool working;

        public UninstallForm()
        {
            Text = "AutoMicSwitch 卸载";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 196);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 251, 253);
            ForeColor = Color.FromArgb(22, 28, 38);

            Label title = new Label();
            title.Text = "卸载 AutoMicSwitch";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(14, 74, 108);
            title.SetBounds(24, 20, 382, 28);
            Controls.Add(title);

            Label info = new Label();
            info.Text = "将移除程序文件、运行库、快捷方式、开机自启和卸载登记。\n"
                      + "安装目录里的 rules.txt、日志和界面缓存会一并删除，需要保留请先备份。";
            info.ForeColor = Color.FromArgb(108, 120, 136);
            info.SetBounds(26, 54, 382, 46);
            Controls.Add(info);

            status.Text = "";
            status.ForeColor = Color.FromArgb(108, 120, 136);
            status.SetBounds(26, 106, 382, 20);
            Controls.Add(status);

            btnGo.Text = "卸载";
            btnGo.SetBounds(216, 144, 88, 34);
            btnGo.FlatStyle = FlatStyle.Flat;
            btnGo.BackColor = Color.FromArgb(192, 71, 63);
            btnGo.ForeColor = Color.White;
            btnGo.FlatAppearance.BorderSize = 0;
            btnGo.Click += delegate { Go(); };
            Controls.Add(btnGo);

            btnCancel.Text = "取消";
            btnCancel.SetBounds(314, 144, 88, 34);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = Color.FromArgb(236, 240, 245);
            btnCancel.ForeColor = Color.FromArgb(22, 28, 38);
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(214, 221, 230);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            AcceptButton = btnGo;
            CancelButton = btnCancel;
        }

        void Go()
        {
            if (working) return;
            working = true;
            btnGo.Enabled = false;
            btnCancel.Enabled = false;

            bool ok = Setup.DoUninstall(delegate (string s) { status.Text = s + "…"; status.Refresh(); });

            if (ok)
            {
                MessageBox.Show("已卸载。", "AutoMicSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            else
            {
                MessageBox.Show(status.Text, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }
}
