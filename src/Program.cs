// Program.cs — 程序入口、托盘菜单、界面数据接口
//
// 编译：见项目根目录 build.cmd
// 运行：AutoMicSwitch.exe [--startup]
//   --startup  静默启动，只驻留托盘，不打开界面（开机自启用）
//
// 界面从 2.3 起改成窗口内的 WebView2，不再往系统浏览器里丢。
// 这里只负责备数据：Api.Route 返回一份状态 JSON，通道是什么它不关心。
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

namespace AutoMicSwitch
{
    internal static class Program
    {
        // 版本号在 AssemblyInfo.cs 里维护，这里读出来给界面和更新检查用
        public static readonly string Version = FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "AutoMicSwitch";
        public const string MutexName = "AutoMicSwitch_SingleInstance";

        // 第二个实例靠这个名字的事件把已经跑着的那个叫到前台。
        // 用命名事件而不是给窗口发广播消息：广播对 WM_USER 以上的自定义消息
        // 会受 UIPI 拦，实测收不到；事件跟有没有窗口无关，静默启动时也照样管用。
        public const string WakeEventName = "AutoMicSwitch_Wake";

        // 托盘气泡由 TrayContext 接管，别的模块（窗口、更新器）通过它冒泡
        public static Action<string, string> NotifyTray;

        static string FormatVersion(Version v)
        {
            if (v == null) return "0.0.0";
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            Mutex mtx = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 已经有一个在跑（可能只是蹲在托盘里），喊它露脸，自己就退
                SignalRunningInstance();
                return;
            }

            bool hidden = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--startup" || s == "--hidden" || s == "-s") hidden = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(hidden));
            GC.KeepAlive(mtx);
        }

        // 拿到（或创建）那个命名事件并置位。同名事件已存在时这里就是打开它。
        static void SignalRunningInstance()
        {
            try
            {
                using (EventWaitHandle e = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName))
                    e.Set();
            }
            catch { }
        }

        // ---------- 运行架构 ----------
        // 主程序是 AnyCPU：同一个 exe 在不同机器上进程位数不一样。
        // 这里把「系统架构」和「当前进程位数」分开报，界面上一眼能看出跑的是什么。
        public static string ArchText
        {
            get
            {
                string os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
                if (string.IsNullOrEmpty(os)) os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
                os = (os == null ? "" : os).ToUpperInvariant();

                string sys;
                if (os.IndexOf("ARM64", StringComparison.Ordinal) >= 0) sys = "ARM64";
                else if (os.IndexOf("AMD64", StringComparison.Ordinal) >= 0) sys = "x64";
                else if (os.IndexOf("X86", StringComparison.Ordinal) >= 0) sys = "x86";
                else sys = "未知";

                if (IntPtr.Size == 8) return sys + " · 64 位";
                if (sys == "x86") return sys + " · 32 位";
                return sys + " · 32 位仿真";
            }
        }

        // 外链一律交给系统浏览器，别让应用窗口变成浏览器
        public static void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            string u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(u); }
            catch { }
        }

        // ---------- 开机自启 ----------
        public static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        public static bool SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (on)
                    {
                        string exe = Application.ExecutablePath;
                        k.SetValue(RunValue, "\"" + exe + "\" --startup", RegistryValueKind.String);
                    }
                    else if (k.GetValue(RunValue) != null)
                    {
                        k.DeleteValue(RunValue, false);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // 自启项记的是绝对路径，程序被挪了位置（比如从便携版换成安装版）就失效，
        // 启动时顺手对一遍，发现对不上就改过来
        public static void SyncAutostartPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return;
                    object cur = k.GetValue(RunValue);
                    if (cur == null) return;
                    string want = "\"" + Application.ExecutablePath + "\" --startup";
                    if (string.Equals((string)cur, want, StringComparison.OrdinalIgnoreCase)) return;
                    using (RegistryKey w = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (w != null) w.SetValue(RunValue, want, RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }

        public static string LoadDashboardHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("Dashboard.html"))
                {
                    if (s != null)
                    {
                        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                            return r.ReadToEnd();
                    }
                }
            }
            catch { }
            // 回退：从 exe 同目录 / src 目录读取
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string[] cand = new string[] { Path.Combine(dir, "Dashboard.html"), Path.Combine(dir, "src", "Dashboard.html") };
                foreach (string p in cand) if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
            }
            catch { }
            return "<!DOCTYPE html><meta charset='utf-8'><body style='background:#0B0E14;color:#E6EDF7;font-family:sans-serif;padding:40px'>"
                 + "<h2>Dashboard.html 未找到</h2><p>请确认已用 build.cmd 重新编译（会把页面嵌入 exe）。</p></body>";
        }
    }

    internal class TrayContext : ApplicationContext
    {
        readonly AudioEngine engine;
        readonly EventWaitHandle wakeEvent;
        NotifyIcon tray;
        MenuItem miPause;
        MenuItem miAuto;
        MenuItem miThemeAuto;
        MenuItem miThemeLight;
        MenuItem miThemeDark;
        MainWindow window;

        // 更新检查跑在后台线程，回调要回到 UI 线程，借一个隐藏控件做转发
        readonly Control dispatcher = new Control();

        public TrayContext(bool hidden)
        {
            IntPtr handle = dispatcher.Handle;   // 强制建句柄，BeginInvoke 才有地方落
            GC.KeepAlive(handle);

            engine = new AudioEngine();
            Api.Engine = engine;

            Program.SyncAutostartPath();

            engine.Start();
            engine.Log("ok", "已启动，v" + Program.Version + " · " + Program.ArchText);

            miPause = new MenuItem("暂停联动");
            miPause.Click += delegate
            {
                if (engine.IsPaused) engine.Resume(); else engine.Pause();
                RefreshMenu();
            };

            miAuto = new MenuItem("开机自启");
            miAuto.Click += delegate
            {
                bool next = !Program.IsAutostartEnabled();
                Program.SetAutostart(next);
                RefreshMenu();
            };

            miThemeAuto = new MenuItem("跟随系统");
            miThemeLight = new MenuItem("浅色");
            miThemeDark = new MenuItem("深色");
            miThemeAuto.Click += delegate { SetTheme(Theme.Auto); };
            miThemeLight.Click += delegate { SetTheme(Theme.Light); };
            miThemeDark.Click += delegate { SetTheme(Theme.Dark); };
            miThemeAuto.RadioCheck = miThemeLight.RadioCheck = miThemeDark.RadioCheck = true;

            MenuItem miTheme = new MenuItem("界面主题");
            miTheme.MenuItems.Add(miThemeAuto);
            miTheme.MenuItems.Add(miThemeLight);
            miTheme.MenuItems.Add(miThemeDark);

            MenuItem miOpen = new MenuItem("打开主界面");
            miOpen.Click += delegate { OpenWindow(); };

            MenuItem miUpdate = new MenuItem("检查更新");
            miUpdate.Click += delegate { CheckUpdate(); };

            MenuItem miLog = new MenuItem("打开日志");
            miLog.Click += delegate { OpenPath(engine.LogPath); };

            MenuItem miDir = new MenuItem("打开程序目录");
            miDir.Click += delegate { OpenPath(AppDomain.CurrentDomain.BaseDirectory); };

            MenuItem miExit = new MenuItem("退出");
            miExit.Click += delegate { Shutdown(); };

            MenuItem[] items = new MenuItem[]
            {
                miOpen, new MenuItem("-"),
                miPause, miAuto, miTheme, new MenuItem("-"),
                miUpdate, miLog, miDir, new MenuItem("-"),
                miExit
            };

            tray = new NotifyIcon();
            tray.Icon = AppIcon.Create(32);
            tray.Text = "AutoMicSwitch · 音频联动";
            tray.Visible = true;
            tray.ContextMenu = new ContextMenu(items);
            tray.DoubleClick += delegate { OpenWindow(); };

            Program.NotifyTray = delegate (string title, string text)
            {
                OnUi(delegate { ShowBalloon(title, text); });
            };

            // 页面里点主题、托盘里点主题、代码里改偏好，都从这里统一收口：
            // 菜单对勾和窗口标题栏一起跟上，不会出现「页面换了、标题栏还是白的」
            Theme.Changed = delegate
            {
                OnUi(delegate
                {
                    RefreshMenu();
                    if (window != null && !window.IsDisposed) window.ApplyTheme();
                });
            };

            RefreshMenu();

            // 更新回调来自后台线程，统一切回 UI 线程再碰托盘和退出
            Updater.Notify = delegate (string title, string text)
            {
                OnUi(delegate { ShowBalloon(title, text); });
            };
            Updater.Applied = delegate
            {
                OnUi(delegate { Shutdown(); });
            };
            Updater.Init(engine);

            // 第二个实例会置位这个事件，值守线程收到就把界面叫出来。
            // 静默启动（--startup）时窗口根本还没建，走事件一样能把它叫起来。
            wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.WakeEventName);
            Thread waiter = new Thread(WakeLoop);
            waiter.IsBackground = true;
            waiter.Name = "Wake";
            waiter.Start();

            if (!hidden) OpenWindow();
        }

        void WakeLoop()
        {
            while (true)
            {
                try
                {
                    wakeEvent.WaitOne();
                    OnUi(delegate { OpenWindow(); });
                }
                catch { break; }   // 事件被释放说明程序在退出了
            }
        }

        void OnUi(MethodInvoker action)
        {
            try { dispatcher.BeginInvoke(action); }
            catch { }
        }

        void SetTheme(string preference)
        {
            // 剩下的（菜单对勾、窗口标题栏、页面）都由 Theme.Changed 接走
            Theme.SetPreference(preference);
        }

        void CheckUpdate()
        {
            ShowBalloon("AutoMicSwitch", "正在检查更新…");
            Updater.Check(true);
        }

        void ShowBalloon(string title, string text)
        {
            try
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(5000);
            }
            catch { }
        }

        void RefreshMenu()
        {
            miPause.Text = engine.IsPaused ? "恢复联动" : "暂停联动";
            miAuto.Checked = Program.IsAutostartEnabled();

            string pref = Theme.Preference;
            miThemeAuto.Checked = pref == Theme.Auto;
            miThemeLight.Checked = pref == Theme.Light;
            miThemeDark.Checked = pref == Theme.Dark;
        }

        // 界面窗口按需创建：以 --startup 静默起的时候根本不需要它，
        // 省下 WebView2 那一份内存
        void OpenWindow()
        {
            if (window == null || window.IsDisposed) window = new MainWindow();
            RefreshMenu();
            window.ShowWindow();
        }

        static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch { }
        }

        void Shutdown()
        {
            try { engine.Stop(); } catch { }
            try
            {
                if (window != null && !window.IsDisposed)
                {
                    window.AllowClose = true;
                    window.Close();
                    window.Dispose();
                    window = null;
                }
            }
            catch { }
            try { if (wakeEvent != null) wakeEvent.Close(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            ExitThread();
        }
    }

    // ---------- 界面数据接口 ----------
    // 通道（HTTP / postMessage）已经不重要了，这里只管把状态拼成 JSON
    internal static class Api
    {
        public static AudioEngine Engine;

        public static string Route(string method, string path, string body)
        {
            try
            {
                if (path == "/api/status") return Status();
                if (path == "/api/action") return Action(body);
                return "{\"ok\":false,\"error\":\"not found\"}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + Json.Str(e.Message) + "}";
            }
        }

        static string Status()
        {
            AudioEngine e = Engine;
            EngineState s = e.State;

            string playbackName, playbackVid, playbackInst, micName, micVid, micInst;
            bool paused, micMatched;
            string matchedText; bool isFallback;
            int switches, retries; DateTime start; long tick; string lastText, lastLevel;
            lock (e.Sync)
            {
                playbackName = s.PlaybackName; playbackVid = s.PlaybackVid; playbackInst = s.PlaybackInstanceId;
                micName = s.MicName; micVid = s.MicVid; micInst = s.MicInstanceId;
                paused = s.Paused; micMatched = s.MicMatched;
                matchedText = s.MatchedText; isFallback = s.IsFallback;
                switches = s.Switches; retries = s.Retries; start = s.StartTime;
                tick = s.LastEventTick; lastText = s.LastEventText; lastLevel = s.LastEventLevel;
            }

            List<DeviceInfo> renders = e.Enumerate(Flow.Render);
            List<DeviceInfo> captures = e.Enumerate(Flow.Capture);
            DeviceInfo defRender = e.GetDefault(Flow.Render, Role.Multimedia);
            DeviceInfo defCapture = e.GetDefault(Flow.Capture, Role.Console);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{");
            sb.Append("\"ok\":true,");
            sb.Append("\"version\":").Append(Json.Str(Program.Version)).Append(",");
            sb.Append("\"arch\":").Append(Json.Str(Program.ArchText)).Append(",");
            sb.Append("\"paused\":").Append(Json.Bool(paused)).Append(",");
            sb.Append("\"uptime\":").Append(Json.Num((long)(DateTime.Now - start).TotalSeconds)).Append(",");
            sb.Append("\"autostart\":").Append(Json.Bool(Program.IsAutostartEnabled())).Append(",");
            sb.Append("\"exePath\":").Append(Json.Str(Application.ExecutablePath)).Append(",");
            sb.Append("\"logPath\":").Append(Json.Str(e.LogPath)).Append(",");
            sb.Append("\"configPath\":").Append(Json.Str(e.ConfigPath)).Append(",");
            sb.Append("\"installed\":").Append(Json.Bool(IsInstalled())).Append(",");

            // 主题：preference 是用户选的，dark 是实际生效的那套
            sb.Append("\"theme\":").Append(Json.Str(Theme.Preference)).Append(",");
            sb.Append("\"themeDark\":").Append(Json.Bool(Theme.IsDark)).Append(",");

            UpdateState us = Updater.Snapshot();
            sb.Append("\"update\":{");
            sb.Append("\"status\":").Append(Json.Str(us.Status)).Append(",");
            sb.Append("\"latest\":").Append(Json.Str(us.Latest)).Append(",");
            sb.Append("\"notes\":").Append(Json.Str(us.Notes)).Append(",");
            sb.Append("\"percent\":").Append(Json.Num(us.Percent)).Append(",");
            sb.Append("\"message\":").Append(Json.Str(us.Message)).Append(",");
            sb.Append("\"tick\":").Append(Json.Num(us.Tick));
            sb.Append("},");

            sb.Append("\"playback\":{");
            sb.Append("\"name\":").Append(Json.Str(playbackName)).Append(",");
            sb.Append("\"vid\":").Append(Json.Str(playbackVid)).Append(",");
            sb.Append("\"instanceId\":").Append(Json.Str(playbackInst));
            sb.Append("},");

            sb.Append("\"mic\":{");
            sb.Append("\"name\":").Append(Json.Str(micName)).Append(",");
            sb.Append("\"vid\":").Append(Json.Str(micVid)).Append(",");
            sb.Append("\"instanceId\":").Append(Json.Str(micInst)).Append(",");
            sb.Append("\"matched\":").Append(Json.Bool(micMatched));
            sb.Append("},");

            sb.Append("\"matchedText\":").Append(Json.Str(matchedText)).Append(",");
            sb.Append("\"isFallback\":").Append(Json.Bool(isFallback)).Append(",");
            sb.Append("\"fallback\":").Append(Json.Str(e.GetFallback())).Append(",");
            sb.Append("\"switches\":").Append(Json.Num(switches)).Append(",");
            sb.Append("\"retries\":").Append(Json.Num(retries)).Append(",");

            sb.Append("\"lastEvent\":{");
            sb.Append("\"text\":").Append(Json.Str(lastText)).Append(",");
            sb.Append("\"level\":").Append(Json.Str(lastLevel)).Append(",");
            sb.Append("\"tick\":").Append(Json.Num(tick));
            sb.Append("},");

            sb.Append("\"rules\":[");
            List<Rule> rules = e.GetRules();
            for (int i = 0; i < rules.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"playback\":").Append(Json.Str(rules[i].PlaybackKey))
                  .Append(",\"mic\":").Append(Json.Str(rules[i].MicKey))
                  .Append(",\"note\":").Append(Json.Str(rules[i].Note)).Append("}");
            }
            sb.Append("],");

            AppendDevices(sb, "devicesPlayback", renders, defRender);
            sb.Append(",");
            AppendDevices(sb, "devicesCapture", captures, defCapture);
            sb.Append(",");

            sb.Append("\"log\":[");
            List<LogEntry> logs = e.SnapshotLog(80);
            for (int i = 0; i < logs.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"t\":").Append(Json.Str(logs[i].Time.ToString("HH:mm:ss")))
                  .Append(",\"level\":").Append(Json.Str(logs[i].Level))
                  .Append(",\"text\":").Append(Json.Str(logs[i].Text)).Append("}");
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        static void AppendDevices(StringBuilder sb, string key, List<DeviceInfo> list, DeviceInfo def)
        {
            sb.Append("\"").Append(key).Append("\":[");
            for (int i = 0; i < list.Count; i++)
            {
                DeviceInfo d = list[i];
                bool isDef = def != null && def.Id == d.Id;
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":").Append(Json.Str(d.Name))
                  .Append(",\"vid\":").Append(Json.Str(d.Vid))
                  .Append(",\"id\":").Append(Json.Str(d.Id))
                  .Append(",\"default\":").Append(Json.Bool(isDef)).Append("}");
            }
            sb.Append("]");
        }

        static string Action(string body)
        {
            string action = Json.Pick(body, "action");
            bool value = Json.PickBool(body, "value", true);

            if (action == "pause") Engine.Pause();
            else if (action == "resume") Engine.Resume();
            else if (action == "toggle")
            {
                if (Engine.IsPaused) Engine.Resume(); else Engine.Pause();
            }
            else if (action == "setplayback") Engine.SetDefaultDevice(Flow.Render, Json.Pick(body, "id"));
            else if (action == "setcapture") Engine.SetDefaultDevice(Flow.Capture, Json.Pick(body, "id"));
            else if (action == "autostart")
            {
                bool ok = Program.SetAutostart(value);
                Engine.Log(ok ? "ok" : "err", "开机自启已" + (value ? "开启" : "关闭"));
            }
            else if (action == "settheme")
            {
                Theme.SetPreference(Json.Pick(body, "text"));
                Engine.Log("info", "界面主题已切到「" + ThemeLabel(Theme.Preference) + "」");
            }
            else if (action == "saverules")
            {
                // 界面把整张规则表打包成若干行发过来，每行三段用制表符隔开
                List<Rule> list = new List<Rule>();
                string[] lines = Json.Pick(body, "text").Replace("\r\n", "\n").Split('\n');
                foreach (string line in lines)
                {
                    if (line.Length == 0) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string pk = parts[0].Trim();
                    string mk = parts[1].Trim();
                    if (pk.Length == 0 || mk.Length == 0) continue;
                    list.Add(new Rule(pk, mk, parts.Length >= 3 ? parts[2].Trim() : ""));
                }

                string err;
                if (!Engine.SaveRules(list, out err)) Engine.Log("err", "规则保存失败：" + err);
            }
            else if (action == "setfallback")
            {
                string err;
                string key = Json.Pick(body, "text");
                if (!Engine.SetFallback(key, out err)) Engine.Log("err", "兜底规则保存失败：" + err);
                else Engine.Log("ok", string.IsNullOrEmpty(key) ? "已清除兜底规则" : "兜底麦克风已设为 " + key);
            }
            else if (action == "openlog")
            {
                try { Process.Start("explorer.exe", "\"" + Engine.LogPath + "\""); } catch { }
            }
            else if (action == "opendir")
            {
                try { Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); } catch { }
            }
            else if (action == "checkupdate") Updater.Check(true);
            else if (action == "downloadupdate") Updater.Download();
            else if (action == "applyupdate") Updater.Apply();
            else if (action == "openreleases")
            {
                Program.OpenExternal("https://github.com/" + Updater.Repo + "/releases");
            }

            return Status();
        }

        static string ThemeLabel(string pref)
        {
            if (pref == Theme.Light) return "浅色";
            if (pref == Theme.Dark) return "深色";
            return "跟随系统";
        }

        // 装没装过：安装版落在 %LOCALAPPDATA%\AutoMicSwitch 下
        static bool IsInstalled()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoMicSwitch");
                return Application.ExecutablePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
