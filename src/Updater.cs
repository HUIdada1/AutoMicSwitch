// Updater.cs — 在线更新
//
// 版本信息读 GitHub Release 里的 latest.yml 直链：latest 永远指向最新一版，
// 不用调 API，也就没有频率限制。直连不通时按候选逐个试本机常见代理端口，
// 候选取的是开源里通用的那几组，不针对某个代理软件。
//
// 单文件 exe 没法自己覆盖自己：下载完写一个 cmd，等本进程退出再换文件、重启。
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AutoMicSwitch
{
    // 更新状态快照，控制台和托盘都读它
    public class UpdateState
    {
        public string Status = "idle";   // idle | checking | latest | available | downloading | ready | error
        public string Latest = "";
        public string Notes = "";
        public int Percent = 0;
        public string Message = "";
        public long Tick = 0;
    }

    public static class Updater
    {
        public const string Repo = "HUIdada1/AutoMicSwitch";

        const string ReleasesBase = "https://github.com/" + Repo + "/releases";
        const string LatestYmlUrl = ReleasesBase + "/latest/download/latest.yml";
        const string LatestSetupUrl = ReleasesBase + "/latest/download/AutoMicSwitch-Setup.exe";

        const int FetchTimeoutMs = 12000;
        const int CheckIntervalMs = 60 * 60 * 1000;  // 后台每小时查一次
        const int FirstCheckDelayMs = 60 * 1000;     // 启动一分钟后再查，避开启动高峰
        const int ManualCooldownMs = 20 * 1000;

        // 直连失败后依次试的本机代理端口
        static readonly string[] ProxyCandidates = new string[]
        {
            "http://127.0.0.1:7890",     // Clash 混合端口
            "http://127.0.0.1:7897",     // Clash Verge
            "http://127.0.0.1:10809",    // v2rayN HTTP
            "http://127.0.0.1:1080",     // 常见本地代理
            "http://127.0.0.1:8118",     // Privoxy
        };

        static readonly object gate = new object();
        static readonly UpdateState state = new UpdateState();

        static AudioEngine engine;
        static Timer timer;
        static bool busy;
        static long lastManualCheck;
        static string lastNotified = "";
        static string lastYml = "";
        static string downloadedFile;

        // 由托盘接管：状态变化、点通知时回调
        public static Action<string, string> Notify;
        public static Action Applied;   // 替换脚本已拉起，该退出了

        public static string CurrentVersion { get { return Program.Version; } }

        public static UpdateState Snapshot()
        {
            lock (gate)
            {
                UpdateState s = new UpdateState();
                s.Status = state.Status;
                s.Latest = state.Latest;
                s.Notes = state.Notes;
                s.Percent = state.Percent;
                s.Message = state.Message;
                s.Tick = state.Tick;
                return s;
            }
        }

        static void SetState(string status, string message, int percent, string latest, string notes)
        {
            lock (gate)
            {
                state.Status = status;
                state.Message = message == null ? "" : message;
                state.Percent = percent;
                if (latest != null) state.Latest = latest;
                if (notes != null) state.Notes = notes;
                state.Tick++;
            }
        }

        // ---------- 生命周期 ----------
        public static void Init(AudioEngine eng)
        {
            engine = eng;
            // 上一轮更新留下的临时目录，启动时顺手清掉
            try
            {
                string dir = TempDir();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }

            int delay = FirstCheckDelayMs;
            timer = new Timer(delegate { Tick(); }, null, delay, CheckIntervalMs);
        }

        static void Tick()
        {
            UpdateState s = Snapshot();
            if (s.Status == "downloading") return;
            Check(false);
        }

        // ---------- 检查 ----------
        public static void Check(bool manual)
        {
            lock (gate)
            {
                if (busy) return;
                if (manual)
                {
                    long now = Environment.TickCount;
                    if (now - lastManualCheck < ManualCooldownMs && state.Status != "error")
                    {
                        state.Message = "刚刚检查过，请稍后再试";
                        state.Tick++;
                        return;
                    }
                    lastManualCheck = now;
                }
                busy = true;
            }

            SetState("checking", "正在检查更新…", 0, null, null);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { DoCheck(manual); }
                catch (Exception e) { SetState("error", "检查更新失败：" + e.Message, 0, null, null); }
                finally { lock (gate) { busy = false; } }
            });
        }

        static void DoCheck(bool manual)
        {
            string err;
            string yml = FetchText(LatestYmlUrl, out err);
            if (yml == null)
            {
                Fail("检查更新失败：" + (err == null ? "网络不可用" : err) + "。可前往 GitHub 手动下载", manual);
                return;
            }
            lastYml = yml;

            string latest = ParseVersion(yml);
            if (string.IsNullOrEmpty(latest))
            {
                Fail("版本信息格式异常，可前往 GitHub 手动下载", manual);
                return;
            }

            if (CompareVersion(latest, Program.Version) > 0)
            {
                SetState("available", "", 0, latest, ParseNotes(yml));
                if (manual) Notify("发现新版本 " + latest, "控制台里可以下载并重启更新");
                else NotifyAvailable(latest);
            }
            else
            {
                SetState("latest", "已是最新版本", 0, "", "");
                // 自动检查不必每次都报平安，手动点的才回一声
                if (manual) Notify("已是最新版本", "当前 v" + Program.Version);
            }
        }

        static void Fail(string message, bool manual)
        {
            SetState("error", message, 0, null, null);
            if (manual) Notify("检查更新失败", message);
        }

        static void NotifyAvailable(string version)
        {
            if (lastNotified == version) return;
            lastNotified = version;
            if (Notify != null)
            {
                try { Notify("发现新版本 " + version, "控制台里可以下载并重启更新"); }
                catch { }
            }
        }

        // ---------- 下载 ----------
        public static void Download()
        {
            lock (gate)
            {
                if (busy || state.Status != "available") return;
                busy = true;
            }

            SetState("downloading", "正在下载…", 0, null, null);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { DoDownload(); }
                catch (Exception e) { SetState("error", "下载更新失败：" + e.Message, 0, null, null); }
                finally { lock (gate) { busy = false; } }
            });
        }

        static void DoDownload()
        {
            string dir = TempDir();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "AutoMicSwitch-Setup.exe");

            string err;
            if (!FetchFile(LatestSetupUrl, file, out err))
            {
                SetState("error", "下载更新失败：" + (err == null ? "网络不可用" : err), 0, null, null);
                return;
            }

            // latest.yml 里带了 sha512 就核一下，防止半截包
            string expect = ParseSha512(lastYml);
            if (!string.IsNullOrEmpty(expect))
            {
                string actual = Sha512Base64(file);
                if (!string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    SetState("error", "更新包校验不通过，已丢弃", 0, null, null);
                    return;
                }
            }

            downloadedFile = file;
            SetState("ready", "新版本已下载，点「重启并更新」即可换上", 100, null, null);
            if (Notify != null)
            {
                try { Notify("新版本已就绪", "点控制台里的「重启并更新」就会自动装上"); }
                catch { }
            }
        }

        // ---------- 应用 ----------
        public static void Apply()
        {
            string file;
            lock (gate)
            {
                if (state.Status != "ready" || downloadedFile == null) return;
                file = downloadedFile;
            }
            if (!File.Exists(file))
            {
                SetState("error", "更新包已丢失，请重新下载", 0, null, null);
                return;
            }

            try
            {
                // 用 cmd start 起，让它脱离本进程。安装器开工第一件事就是杀掉旧程序，
                // 直接 Process.Start 的话父子同命，安装会断在半路。
                // 静默装完由 /relaunch 把新版本拉起来。
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c start \"\" \"" + file + "\" /S /relaunch");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);

                if (engine != null) engine.Log("ok", "更新已就绪，正在退出，交给安装程序换上…");
                if (Applied != null) Applied();
            }
            catch (Exception e)
            {
                SetState("error", "启动安装程序失败：" + e.Message, 0, null, null);
            }
        }

        static string TempDir()
        {
            return Path.Combine(Path.GetTempPath(), "AutoMicSwitch-update");
        }

        // ---------- 网络 ----------
        // 直连 → 候选代理逐个试；只有网络类错误才值得换代理，
        // HTTP 404 这种换到哪个代理都一样，直接收工
        static string FetchText(string url, out string err)
        {
            err = null;
            for (int i = -1; i < ProxyCandidates.Length; i++)
            {
                string proxy = i < 0 ? null : ProxyCandidates[i];
                try { return HttpGetText(url, proxy); }
                catch (Exception e)
                {
                    err = FriendlyError(e);
                    if (!IsNetworkError(e)) return null;
                }
            }
            return null;
        }

        static bool FetchFile(string url, string path, out string err)
        {
            err = null;
            for (int i = -1; i < ProxyCandidates.Length; i++)
            {
                string proxy = i < 0 ? null : ProxyCandidates[i];
                try { HttpGetFile(url, proxy, path); return true; }
                catch (Exception e)
                {
                    err = FriendlyError(e);
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    if (!IsNetworkError(e)) return false;
                }
            }
            return false;
        }

        static string HttpGetText(string url, string proxy)
        {
            HttpWebRequest req = MakeRequest(url, proxy);
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static void HttpGetFile(string url, string proxy, string path)
        {
            HttpWebRequest req = MakeRequest(url, proxy);
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[64 * 1024];
                long total = resp.ContentLength;
                long got = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    got += n;
                    if (total > 0)
                    {
                        int pct = (int)(got * 100 / total);
                        if (pct > 99) pct = 99;
                        SetState("downloading", "正在下载…", pct, null, null);
                    }
                }
            }
        }

        static HttpWebRequest MakeRequest(string url, string proxy)
        {
            // .NET 4.x 默认只协商到 TLS 1.0，GitHub 那边要 1.2
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "AutoMicSwitch/" + Program.Version;
            req.Timeout = FetchTimeoutMs;
            req.ReadWriteTimeout = FetchTimeoutMs;
            req.AllowAutoRedirect = true;
            req.Proxy = proxy == null ? null : new WebProxy(proxy);
            return req;
        }

        static bool IsNetworkError(Exception e)
        {
            WebException we = e as WebException;
            if (we == null) return true;
            // 拿到了 HTTP 响应说明连接本身是通的，换代理也没用
            return we.Status != WebExceptionStatus.ProtocolError;
        }

        static string FriendlyError(Exception e)
        {
            WebException we = e as WebException;
            if (we != null && we.Status == WebExceptionStatus.ProtocolError && we.Response != null)
            {
                try
                {
                    HttpWebResponse r = (HttpWebResponse)we.Response;
                    if ((int)r.StatusCode == 404) return "还没有发布过版本";
                    return "HTTP " + (int)r.StatusCode;
                }
                catch { }
            }
            if (e is WebException) return "连接失败（直连和本机代理都试过了）";
            return e.Message;
        }

        // ---------- latest.yml 解析 ----------
        static string ParseVersion(string yml)
        {
            Match m = Regex.Match(yml, @"(?m)^version:\s*([^\s]+)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static string ParseSha512(string yml)
        {
            if (string.IsNullOrEmpty(yml)) return "";
            Match m = Regex.Match(yml, @"(?m)^sha512:\s*([^\s]+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // releaseNotes 用块标量写，这里按缩进把它整段取回来
        static string ParseNotes(string yml)
        {
            if (string.IsNullOrEmpty(yml)) return "";
            string[] lines = yml.Replace("\r\n", "\n").Split('\n');
            int idx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("releaseNotes:")) { idx = i; break; }
            }
            if (idx < 0) return "";

            string inline = lines[idx].Substring("releaseNotes:".Length).Trim();
            if (inline.Length == 0 || (inline[0] != '|' && inline[0] != '>'))
                return inline.Trim().Trim('"', '\'');

            List<string> block = new List<string>();
            for (int i = idx + 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Trim().Length == 0) block.Add("");
                else if (l[0] == ' ' || l[0] == '\t') block.Add(l);
                else break;
            }
            while (block.Count > 0 && block[block.Count - 1].Length == 0) block.RemoveAt(block.Count - 1);
            if (block.Count == 0) return "";

            int pad = int.MaxValue;
            foreach (string l in block)
            {
                if (l.Trim().Length == 0) continue;
                int lead = 0;
                while (lead < l.Length && (l[lead] == ' ' || l[lead] == '\t')) lead++;
                if (lead < pad) pad = lead;
            }
            StringBuilder sb = new StringBuilder();
            foreach (string l in block)
                sb.AppendLine(l.Length >= pad ? l.Substring(pad) : l.TrimStart());
            return sb.ToString().Trim();
        }

        public static int CompareVersion(string a, string b)
        {
            string[] pa = (a == null ? "" : a).Split('.');
            string[] pb = (b == null ? "" : b).Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? LeadingInt(pa[i]) : 0;
                int y = i < pb.Length ? LeadingInt(pb[i]) : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        static int LeadingInt(string s)
        {
            int v = 0, i = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; }
            return v;
        }

        static string Sha512Base64(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA512 sha = SHA512.Create())
                return Convert.ToBase64String(sha.ComputeHash(fs));
        }
    }
}
