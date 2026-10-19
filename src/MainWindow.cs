// MainWindow.cs — 主界面窗口
//
// 界面是嵌在窗口里的 WebView2，不再往系统浏览器里丢。
// 页面和 C# 之间走 postMessage：页面发 {i,p,a,v,d,t}，这边回 {i,d:<状态JSON>}。
// 状态 JSON 复用 Api.Route，跟原来 HTTP 那套是同一份逻辑，只是换了条通道。
//
// WebView2 初始化是异步的，窗口先出来铺主题底色，页面画好了再露脸，中间不闪白。
// 运行时装不上不崩，换成原生提示页，引导装一次。
//
// 作者：沐辉 · MIT License
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutoMicSwitch
{
    internal class MainWindow : Form
    {
        const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        readonly WebView2 view = new WebView2();
        readonly Panel fallback = new Panel();
        readonly Label fbTitle = new Label();
        readonly Label fbBody = new Label();
        readonly Button fbGet = new Button();
        readonly Button fbRetry = new Button();

        CoreWebView2Environment env;
        bool booting;
        bool booted;
        bool trayHintShown;

        // 退出流程会把它置真。平时点 X 只是收进托盘，音频联动还得接着跑。
        public bool AllowClose;

        public MainWindow()
        {
            Text = "AutoMicSwitch";
            Icon = AppIcon.Create(32);
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            DoubleBuffered = true;

            view.Dock = DockStyle.Fill;
            view.Visible = false;   // 等页面画好再露，避免白闪

            BuildFallback();

            Controls.Add(view);
            Controls.Add(fallback);
            fallback.Visible = false;

            Theme.ApplyToWindow(this);
        }

        // ---------- 原生提示页 ----------
        void BuildFallback()
        {
            fallback.Dock = DockStyle.Fill;

            fbTitle.AutoSize = false;
            fbTitle.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            fbTitle.TextAlign = ContentAlignment.MiddleLeft;

            fbBody.AutoSize = false;
            fbBody.Font = new Font("Microsoft YaHei UI", 9.5f);

            fbGet.FlatStyle = FlatStyle.Flat;
            fbGet.FlatAppearance.BorderSize = 0;
            fbGet.ForeColor = Color.White;
            fbGet.Text = "下载 WebView2 运行时";
            fbGet.Click += delegate { Program.OpenExternal(RuntimeDownloadUrl); };

            fbRetry.FlatStyle = FlatStyle.Flat;
            fbRetry.Text = "重试";
            fbRetry.Click += delegate { Retry(); };

            fallback.Controls.Add(fbTitle);
            fallback.Controls.Add(fbBody);
            fallback.Controls.Add(fbGet);
            fallback.Controls.Add(fbRetry);
            fallback.Resize += delegate { LayoutFallback(); };
        }

        void LayoutFallback()
        {
            int w = Math.Min(560, Math.Max(320, fallback.ClientSize.Width - 80));
            int x = (fallback.ClientSize.Width - w) / 2;
            int y = Math.Max(48, fallback.ClientSize.Height / 2 - 130);

            fbTitle.SetBounds(x, y, w, 34);
            fbBody.SetBounds(x, y + 44, w, 108);
            fbGet.SetBounds(x, y + 168, 176, 36);
            fbRetry.SetBounds(x + 192, y + 168, 96, 36);
        }

        void ShowFallback(string message)
        {
            fbBody.Text = message;
            ApplyThemeToFallback();
            LayoutFallback();
            fallback.Visible = true;
            fallback.BringToFront();
            view.Visible = false;
        }

        void ApplyThemeToFallback()
        {
            bool dark = Theme.IsDark;
            Color back = dark ? Theme.DarkBack : Theme.LightBack;
            Color txt = dark ? Color.FromArgb(230, 237, 247) : Color.FromArgb(20, 26, 36);
            Color sub = dark ? Color.FromArgb(140, 154, 173) : Color.FromArgb(92, 104, 121);

            fallback.BackColor = back;
            fbTitle.ForeColor = txt;
            fbBody.ForeColor = sub;
            fbGet.BackColor = dark ? Color.FromArgb(62, 140, 190) : Color.FromArgb(30, 118, 172);
            fbRetry.BackColor = dark ? Color.FromArgb(32, 39, 51) : Color.FromArgb(232, 236, 242);
            fbRetry.ForeColor = txt;
            fbRetry.FlatAppearance.BorderColor = dark ? Color.FromArgb(48, 58, 74) : Color.FromArgb(212, 219, 229);
        }

        void Retry()
        {
            fallback.Visible = false;
            fbBody.Text = "";
            Boot();
        }

        // ---------- 启动 ----------
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Boot();
        }

        async void Boot()
        {
            if (booting || booted) return;
            booting = true;

            try
            {
                // 先单独问一句运行时在不在，缺失时能给出准确说法，
                // 而不是笼统地把异常原文丢给用户
                string runtime = null;
                try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch (Exception re)
                {
                    Log("err", "WebView2 运行时不可用：" + re.Message);
                    ShowFallback("这台机器上没找到 WebView2 运行时，界面需要它才能显示。\n\n"
                        + "点下面的按钮装一次即可，装完回到这里点「重试」。\n"
                        + "Windows 11 自带，Windows 10 也可以让它随 Edge 一起更新。");
                    return;
                }

                if (env == null)
                {
                    // 用户数据放在安装目录下，卸载时跟着一起清掉
                    string udf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2");
                    env = await CoreWebView2Environment.CreateAsync(null, udf);
                }
                await view.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;

                CoreWebView2 core = view.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsWebMessageEnabled = true;

                core.WebMessageReceived += OnWebMessage;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;

                ApplyTheme();
                core.NavigateToString(BuildPage());
                booted = true;
                Log("info", "界面已载入（WebView2 " + runtime + "）");
            }
            catch (Exception e)
            {
                Log("err", "界面初始化失败：" + e.Message);
                ShowFallback("界面没能初始化起来。\n\n" + e.Message
                    + "\n\n如果反复失败，可以从托盘菜单「打开日志」看详细记录。");
            }
            finally { booting = false; }
        }

        // 页面里留了一处主题占位符，喂进去之前先填成当前生效的那套。
        // 「跟随系统」在这里就已经解析成具体值了，首帧就是对的，不会先亮后暗闪一下。
        string BuildPage()
        {
            string html = Program.LoadDashboardHtml();
            return html.Replace("__INITIAL_THEME__", Theme.IsDark ? "dark" : "light");
        }

        // ---------- 页面 → C# ----------
        // 页面发的是扁平结构：i 请求号、p 路径、a 动作、v 布尔值、dv 设备 id、tx 文本
        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(raw)) return;

            string id = Json.Pick(raw, "i");
            string path = Json.Pick(raw, "p");
            if (path.Length == 0) return;

            // 还原成 Api 一直用的请求体，Api 那层不用知道传输方式换过了
            string body = "{\"action\":" + Json.Str(Json.Pick(raw, "a"))
                        + ",\"value\":" + Json.Bool(Json.PickBool(raw, "v", true))
                        + ",\"id\":" + Json.Str(Json.Pick(raw, "dv"))
                        + ",\"text\":" + Json.Str(Json.Pick(raw, "tx")) + "}";

            string json = Api.Route("POST", path, body);
            Post("{\"i\":" + Json.Str(id) + ",\"d\":" + json + "}");
        }

        public void Post(string json)
        {
            if (!booted) return;
            try { view.CoreWebView2.PostWebMessageAsString(json); }
            catch { }
        }

        // ---------- 导航护栏 ----------
        // 页面本身是 NavigateToString 喂进去的 data: URI，只放行它。
        // 真点了外链就交给系统浏览器，不让窗口变成浏览器。
        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string uri = e.Uri;
            if (uri == null) return;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

            e.Cancel = true;
            Program.OpenExternal(uri);
        }

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            Program.OpenExternal(e.Uri);
        }

        void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                view.Visible = true;
                view.BringToFront();
                return;
            }
            Log("err", "界面加载失败：" + e.WebErrorStatus);
            ShowFallback("界面内容没能加载出来（" + e.WebErrorStatus + "）。\n\n"
                + "点「重试」再试一次；还不行就从托盘菜单重新打开窗口。");
        }

        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log("err", "界面进程异常：" + e.ProcessFailedKind);
            booted = false;
            ShowFallback("界面进程意外退出了（" + e.ProcessFailedKind + "）。\n\n"
                + "点「重试」可以重新把它拉起来。");
        }

        // ---------- 主题 ----------
        public void ApplyTheme()
        {
            bool dark = Theme.IsDark;
            Theme.ApplyToWindow(this);

            try { view.DefaultBackgroundColor = dark ? Theme.DarkBack : Theme.LightBack; }
            catch { }

            // 控制器一就绪就设偏好色，别等 booted——首次进入是在页面加载之前调用的，
            // 挂在 booted 后面的话启动那一次会被整个跳过，页面里 prefers-color-scheme 就是默认值
            try
            {
                if (view.CoreWebView2 != null)
                {
                    view.CoreWebView2.Profile.PreferredColorScheme =
                        dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                }
            }
            catch { }

            // 页面自己每秒也会取一次状态，这里推一下让它立刻换，不用等下一轮
            if (booted) Post("{\"e\":\"theme\"}");

            if (fallback.Visible) ApplyThemeToFallback();
        }

        const int WM_SETTINGCHANGE = 0x001A;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE)
            {
                // 系统主题变了，跟随模式下要立刻换过来
                try { ApplyTheme(); }
                catch { }
            }
            base.WndProc(ref m);
        }

        // ---------- 显隐 ----------
        public void ShowWindow()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            Post("{\"e\":\"wake\"}");   // 页面隐藏时会停掉轮询，叫它接着跑
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点 X 只收进托盘：联动得一直跑着才有意义
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    Program.NotifyTray("AutoMicSwitch", "已收进托盘，联动照常运行。双击图标可以再打开。");
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void Log(string level, string text)
        {
            AudioEngine e = Api.Engine;
            if (e == null) return;
            try { e.Log(level, text); }
            catch { }
        }
    }
}
