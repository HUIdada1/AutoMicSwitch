// AppIcon.cs — 应用图标画法
//
// 托盘图标、窗口图标、编译期生成的 .ico 都用这一份画法，改一处三处一起变。
// 按 32x32 的坐标系画，size 决定缩放倍数。
//
// 作者：沐辉 · MIT License
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AutoMicSwitch
{
    public static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        // 单一色相的圆角方块 + 白色话筒。
        // 同色系深浅渐变在小尺寸下比双色渐变干净，16px 也认得出来。
        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);

                using (GraphicsPath path = RoundedRect(new RectangleF(1, 1, 30, 30), 8))
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new RectangleF(0, 0, 32, 32),
                    Color.FromArgb(94, 176, 224), Color.FromArgb(38, 112, 160), 60f))
                    g.FillPath(b, path);

                using (SolidBrush w = new SolidBrush(Color.FromArgb(248, 251, 255)))
                {
                    using (GraphicsPath cap = RoundedRect(new RectangleF(12, 6, 8, 14), 4))
                        g.FillPath(w, cap);
                    g.FillRectangle(w, 14, 20, 4, 4);
                    using (Pen p = new Pen(Color.FromArgb(248, 251, 255), 2.2f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawArc(p, 9, 12, 14, 12, 0, 180);
                        g.DrawLine(p, 16, 24, 16, 27);
                        g.DrawLine(p, 12, 27, 20, 27);
                    }
                }
            }
            return bmp;
        }

        // GetHicon 拿到的是需要自己销毁的句柄，直接 FromHandle 会漏。
        // 克隆一份出来，再把原句柄还回去。
        public static Icon Create(int size)
        {
            using (Bitmap bmp = Render(size))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
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
    }
}
