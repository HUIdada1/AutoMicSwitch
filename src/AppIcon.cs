// AppIcon.cs — 应用图标
//
// 托盘图标、窗口图标、构建期生成的 .ico 都用这份画法，改一处三处一起变。
// 坐标系固定 32×32，size 决定缩放倍数。
//
// 图形：蓝到青的圆角方块 + 白色话筒，小尺寸下也能认出来。
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

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);

                RectangleF tile = new RectangleF(1f, 1f, 30f, 30f);
                using (GraphicsPath path = RoundedRect(tile, 8.4f))
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    tile, Color.FromArgb(59, 130, 246), Color.FromArgb(6, 182, 212), 55f))
                    g.FillPath(fill, path);

                using (SolidBrush white = new SolidBrush(Color.White))
                using (GraphicsPath cap = RoundedRect(new RectangleF(12.3f, 5.9f, 7.4f, 12.6f), 3.7f))
                    g.FillPath(white, cap);

                using (Pen pen = new Pen(Color.White, 2.05f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, 10.1f, 11.5f, 11.8f, 11.8f, 0f, 180f);
                    g.DrawLine(pen, 16f, 23.3f, 16f, 26f);
                    g.DrawLine(pen, 12.7f, 26f, 19.3f, 26f);
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
