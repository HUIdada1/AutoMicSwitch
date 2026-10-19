// Theme.cs — 界面主题
//
// 三态：跟随系统 / 亮 / 暗。偏好写 HKCU，页面和窗口标题栏都跟着它走。
// 系统主题变了会收到 WM_SETTINGCHANGE，跟着切，不用重启。
//
// 作者：沐辉 · MIT License
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoMicSwitch
{
    public static class Theme
    {
        public const string Auto = "auto";
        public const string Light = "light";
        public const string Dark = "dark";

        const string PrefKey = @"Software\AutoMicSwitch";
        const string PrefValue = "Theme";
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        // 窗口内容区底色，跟页面 body 对齐。
        // WebView2 把页面画出来之前会先铺这层底色，两边一致才不会闪白。
        public static readonly Color DarkBack = Color.FromArgb(11, 14, 20);
        public static readonly Color LightBack = Color.FromArgb(246, 247, 249);

        // ---------- 偏好 ----------
        public static string Preference
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PrefKey, false))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue(PrefValue);
                            if (v != null) return Normalize(Convert.ToString(v));
                        }
                    }
                }
                catch { }
                return Auto;
            }
        }

        // 窗口标题栏要跟着换，托盘菜单的对勾也要挪。
        // 由 TrayContext 在启动时接上，接不到也不影响主题本身生效。
        public static Action Changed;

        public static void SetPreference(string value)
        {
            string v = Normalize(value);
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(PrefKey))
                {
                    if (k != null) k.SetValue(PrefValue, v, RegistryValueKind.String);
                }
            }
            catch { }

            if (Changed != null)
            {
                try { Changed(); }
                catch { }
            }
        }

        public static void ClearPreference()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PrefKey, true))
                {
                    if (k != null && k.GetValue(PrefValue) != null) k.DeleteValue(PrefValue, false);
                }
                // 只剩下我们自己的键，顺手清干净
                Registry.CurrentUser.DeleteSubKey(PrefKey, false);
            }
            catch { }
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return Auto;
            string v = value.Trim().ToLowerInvariant();
            if (v == Light) return Light;
            if (v == Dark) return Dark;
            return Auto;
        }

        // ---------- 系统主题 ----------
        // AppsUseLightTheme：1 亮色，0 暗色。读不到就按亮色算，这是 Windows 的默认。
        public static bool SystemIsDark
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue("AppsUseLightTheme");
                            if (v != null) return Convert.ToInt32(v) == 0;
                        }
                    }
                }
                catch { }
                return false;
            }
        }

        // 偏好 + 系统 → 实际用哪套
        public static bool IsDark
        {
            get
            {
                string p = Preference;
                if (p == Dark) return true;
                if (p == Light) return false;
                return SystemIsDark;
            }
        }

        public static Color WindowBack
        {
            get { return IsDark ? DarkBack : LightBack; }
        }

        // ---------- 窗口标题栏 ----------
        // 让系统标题栏跟着主题走。Win10 1809 之后才有，属性号在 1809~1909 是 19，
        // 20H1 起改成 20，两个都试一遍，都不认就当不支持，不影响别的。
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyToWindow(Form form)
        {
            if (form == null) return;
            try
            {
                bool dark = IsDark;
                form.BackColor = dark ? DarkBack : LightBack;

                IntPtr h = form.Handle;
                int flag = dark ? 1 : 0;

                int hr = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, 4);
                if (hr != 0) DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref flag, 4);

                // Win11 才有圆角，不认就算了
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            }
            catch { }
        }
    }
}
