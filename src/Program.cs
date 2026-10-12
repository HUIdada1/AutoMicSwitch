// Program.cs — 程序入口、托盘菜单、本地控制台 API
//
// 编译：见项目根目录 build.cmd
// 运行：AutoMicSwitch.exe [--startup]
//   --startup  静默启动，只驻留托盘，不打开控制台（开机自启用）
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
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

        public const int DefaultPort = 17817;
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "AutoMicSwitch";
        public const string MutexName = "AutoMicSwitch_SingleInstance";

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
            if (!createdNew) return; // 已有实例在跑

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
            return "<!DOCTYPE html><meta charset='utf-8'><body style='background:#070B12;color:#E6EDF7;font-family:sans-serif;padding:40px'>"
                 + "<h2>Dashboard.html 未找到</h2><p>请确认已用 build.cmd 重新编译（会把页面嵌入 exe）。</p></body>";
        }
    }

    internal class TrayContext : ApplicationContext
    {
        readonly AudioEngine engine;
        MiniHttpServer server;
        NotifyIcon tray;
        MenuItem miPause;
        MenuItem miAuto;
        MenuItem miUpdate;

        // 更新检查跑在后台线程，回调要回到 UI 线程，借一个隐藏控件做转发
        readonly Control dispatcher = new Control();

        public TrayContext(bool hidden)
        {
            IntPtr handle = dispatcher.Handle;   // 强制建句柄，BeginInvoke 才有地方落
            GC.KeepAlive(handle);

            engine = new AudioEngine();
            Api.Engine = engine;

            Program.SyncAutostartPath();

            string html = Program.LoadDashboardHtml();
            try
            {
                server = new MiniHttpServer(Program.DefaultPort, html, Api.Route);
                Api.Server = server;
            }
            catch (Exception e)
            {
                server = null;
                Api.Server = null;
                engine.Log("err", "控制台启动失败：" + e.Message);
            }

            engine.Start();
            if (server != null) engine.Log("ok", "控制台已就绪 → http://127.0.0.1:" + server.Port + "/");

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

            MenuItem miOpen = new MenuItem("打开控制台");
            miOpen.Click += delegate { OpenDashboard(); };

            miUpdate = new MenuItem("检查更新");
            miUpdate.Click += delegate { CheckUpdate(); };

            MenuItem miLog = new MenuItem("打开日志");
            miLog.Click += delegate { OpenPath(engine.LogPath); };

            MenuItem miDir = new MenuItem("打开程序目录");
            miDir.Click += delegate { OpenPath(AppDomain.CurrentDomain.BaseDirectory); };

            MenuItem miExit = new MenuItem("退出");
            miExit.Click += delegate { Shutdown(); };

            MenuItem[] items = new MenuItem[]
            {
                miOpen, new MenuItem("-"), miPause, miAuto, new MenuItem("-"), miUpdate, miLog, miDir, new MenuItem("-"), miExit
            };

            tray = new NotifyIcon();
            tray.Icon = BuildTrayIcon();
            tray.Text = "AutoMicSwitch · 音频联动";
            tray.Visible = true;
            tray.ContextMenu = new ContextMenu(items);
            tray.DoubleClick += delegate { OpenDashboard(); };

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

            if (!hidden) OpenDashboard();
        }

        void OnUi(MethodInvoker action)
        {
            try { dispatcher.BeginInvoke(action); }
            catch { }
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
        }

        void OpenDashboard()
        {
            if (server == null)
            {
                MessageBox.Show("控制台服务未启动，请查看日志。", "AutoMicSwitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start("http://127.0.0.1:" + server.Port + "/"); }
            catch { }
        }

        static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch { }
        }

        void Shutdown()
        {
            try { engine.Stop(); } catch { }
            try { if (server != null) server.Stop(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            ExitThread();
        }

        static Icon BuildTrayIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // 圆角底板
                using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, 30, 30), 8))
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(56, 189, 248), Color.FromArgb(99, 102, 241), 45f))
                    g.FillPath(b, path);

                // 麦克风主体
                using (SolidBrush w = new SolidBrush(Color.FromArgb(235, 240, 255)))
                {
                    using (GraphicsPath cap = RoundedRect(new Rectangle(12, 6, 8, 14), 4))
                        g.FillPath(w, cap);
                    g.FillRectangle(w, 14, 20, 4, 4);
                    using (Pen p = new Pen(Color.FromArgb(235, 240, 255), 2.2f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawArc(p, 9, 12, 14, 12, 0, 180);
                        g.DrawLine(p, 16, 24, 16, 27);
                        g.DrawLine(p, 12, 27, 20, 27);
                    }
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------- 本地 API ----------
    internal static class Api
    {
        public static AudioEngine Engine;
        public static MiniHttpServer Server;

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
            sb.Append("\"port\":").Append(Server == null ? 0 : Server.Port).Append(",");
            sb.Append("\"paused\":").Append(Json.Bool(paused)).Append(",");
            sb.Append("\"uptime\":").Append(Json.Num((long)(DateTime.Now - start).TotalSeconds)).Append(",");
            sb.Append("\"autostart\":").Append(Json.Bool(Program.IsAutostartEnabled())).Append(",");
            sb.Append("\"exePath\":").Append(Json.Str(Application.ExecutablePath)).Append(",");
            sb.Append("\"logPath\":").Append(Json.Str(e.LogPath)).Append(",");
            sb.Append("\"configPath\":").Append(Json.Str(e.ConfigPath)).Append(",");
            sb.Append("\"installed\":").Append(Json.Bool(IsInstalled())).Append(",");

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
            string action = Pick(body, "action");
            bool value = PickBool(body, "value", true);

            if (action == "pause") Engine.Pause();
            else if (action == "resume") Engine.Resume();
            else if (action == "toggle")
            {
                if (Engine.IsPaused) Engine.Resume(); else Engine.Pause();
            }
            else if (action == "setplayback") Engine.SetDefaultDevice(Flow.Render, Pick(body, "id"));
            else if (action == "setcapture") Engine.SetDefaultDevice(Flow.Capture, Pick(body, "id"));
            else if (action == "autostart")
            {
                bool ok = Program.SetAutostart(value);
                Engine.Log(ok ? "ok" : "err", "开机自启已" + (value ? "开启" : "关闭"));
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
                try { Process.Start("https://github.com/" + Updater.Repo + "/releases"); } catch { }
            }

            return Status();
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

        static string Pick(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return "";
            int q1 = json.IndexOf('"', c + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        static bool PickBool(string json, string key, bool def)
        {
            if (string.IsNullOrEmpty(json)) return def;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return def;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return def;
            string rest = json.Substring(c + 1).TrimStart();
            if (rest.StartsWith("true")) return true;
            if (rest.StartsWith("false")) return false;
            return def;
        }
    }
}
