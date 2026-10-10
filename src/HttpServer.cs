// HttpServer.cs — 本地控制台的 HTTP 服务
//
// 用 TcpListener 而不是 HttpListener：后者在非管理员下需要先做 urlacl 授权，
// TcpListener 绑 127.0.0.1 不需要任何权限。
//
// 作者：沐辉 · MIT License
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutoMicSwitch
{
    public class MiniHttpServer
    {
        readonly TcpListener listener;
        readonly Thread worker;
        readonly string html;
        readonly Func<string, string, string, string> route; // (method, path, body) => json
        volatile bool running = true;

        public int Port { get; private set; }

        public MiniHttpServer(int preferredPort, string html, Func<string, string, string, string> route)
        {
            this.html = html;
            this.route = route;

            int p = preferredPort;
            TcpListener l = null;
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    TcpListener t = new TcpListener(IPAddress.Loopback, p);
                    t.Start();
                    l = t;
                    break;
                }
                catch { p++; }
            }
            if (l == null) throw new Exception("找不到可用端口 (从 " + preferredPort + " 起)");
            listener = l;
            Port = p;

            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "DashboardHttp";
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }

        void Loop()
        {
            while (running)
            {
                TcpClient c = null;
                try { c = listener.AcceptTcpClient(); }
                catch { break; }
                try { Handle(c); }
                catch { }
                finally { try { c.Close(); } catch { } }
            }
        }

        void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 4000;
            c.SendTimeout = 4000;
            NetworkStream ns = c.GetStream();

            MemoryStream buf = new MemoryStream();
            byte[] tmp = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = ns.Read(tmp, 0, tmp.Length);
                if (n <= 0) return;
                buf.Write(tmp, 0, n);
                headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, "\r\n\r\n");
                if (buf.Length > 1024 * 1024) return; // 防御超大请求
            }

            byte[] all = buf.ToArray();
            string head = Encoding.UTF8.GetString(all, 0, headerEnd);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);

            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string path = first[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string low = lines[i].ToLowerInvariant();
                if (low.StartsWith("content-length:"))
                {
                    int.TryParse(lines[i].Substring(15).Trim(), out contentLength);
                }
            }

            MemoryStream body = new MemoryStream();
            int alreadyBody = all.Length - (headerEnd + 4);
            if (alreadyBody > 0) body.Write(all, headerEnd + 4, alreadyBody);
            while (body.Length < contentLength)
            {
                int n = ns.Read(tmp, 0, tmp.Length);
                if (n <= 0) break;
                body.Write(tmp, 0, n);
            }
            string bodyStr = Encoding.UTF8.GetString(body.ToArray());

            string content;
            string ctype;
            if (path == "/" || path == "/index.html" || path == "/dashboard")
            {
                content = html;
                ctype = "text/html; charset=utf-8";
            }
            else if (path == "/favicon.ico")
            {
                content = "";
                ctype = "image/x-icon";
            }
            else
            {
                content = route(method, path, bodyStr);
                ctype = "application/json; charset=utf-8";
            }

            byte[] payload = Encoding.UTF8.GetBytes(content);
            string header = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: " + ctype + "\r\n"
                + "Content-Length: " + payload.Length + "\r\n"
                + "Cache-Control: no-store, no-cache, must-revalidate\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.UTF8.GetBytes(header);
            ns.Write(headBytes, 0, headBytes.Length);
            if (payload.Length > 0) ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }

        static int IndexOf(byte[] data, int len, string needle)
        {
            byte[] n = Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i <= len - n.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < n.Length; j++)
                {
                    if (data[i + j] != n[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }

    // 极简 JSON 输出工具（手写，避免引入 System.Web 依赖）
    public static class Json
    {
        public static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Str(string s)
        {
            return "\"" + Esc(s) + "\"";
        }

        public static string Bool(bool b) { return b ? "true" : "false"; }

        public static string Num(long n) { return n.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }
}
