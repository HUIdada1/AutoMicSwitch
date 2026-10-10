// AudioEngine.cs — 音频联动引擎
//
// 轮询默认播放设备，按 rules.txt 里的规则切换默认麦克风。
// 设备用 InstanceId 中的 VID 识别，换 USB 口、改设备名、重装驱动都不影响。
//
// 作者：沐辉 · MIT License
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AutoMicSwitch
{
    public enum Flow { Render = 0, Capture = 1 }
    public enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    public class DeviceInfo
    {
        public string Id;
        public string InstanceId;
        public string Name;
        public Flow FlowKind;
        public bool IsDefault;

        public string Vid
        {
            get { return AudioEngine.ExtractVid(InstanceId); }
        }
    }

    public class Rule
    {
        public string PlaybackKey;
        public string MicKey;
        public string Note;

        public Rule(string playbackKey, string micKey, string note)
        {
            PlaybackKey = playbackKey;
            MicKey = micKey;
            Note = note;
        }
    }

    public class LogEntry
    {
        public DateTime Time;
        public string Level;   // info | ok | warn | err
        public string Text;
    }

    // 对外状态快照（读的时候请先 Clone 或锁）
    public class EngineState
    {
        public bool Running = true;
        public bool Paused = false;
        public DateTime StartTime = DateTime.Now;

        public string PlaybackName = "-";
        public string PlaybackInstanceId = "";
        public string PlaybackVid = "-";

        public string MicName = "-";
        public string MicInstanceId = "";
        public string MicVid = "-";

        public string MatchedText = "等待首次切换…";
        public bool IsFallback = false;
        public bool MicMatched = true;

        public int Switches = 0;
        public int Retries = 0;

        public string LastEventText = "";
        public DateTime LastEventTime = DateTime.MinValue;
        public string LastEventLevel = "info";
        public long LastEventTick = 0;   // 单调递增，用于前端判断"有新事件"
    }

    public class AudioEngine
    {
        // 规则全部来自 exe 同目录的 rules.txt，格式见 rules.example.txt。
        // 没有配置规则时，程序不会改动任何设备。

        public const int PollIntervalMs = 700;

        public readonly EngineState State = new EngineState();

        // 状态同步锁（外部读取 State 时请 lock 这个对象，保证与引擎写入一致）
        public object Sync { get { return stateLock; } }

        readonly List<Rule> rules = new List<Rule>();
        readonly object stateLock = new object();
        readonly object logLock = new object();
        readonly object setLock = new object();
        readonly List<LogEntry> logBuffer = new List<LogEntry>();
        const int LogBufferMax = 400;

        string fallbackMicKey;
        public string LogPath;
        public string ConfigPath;
        public bool RulesConfigured;

        Thread thread;
        volatile bool pausedFlag = false;
        volatile bool stopped = false;

        IMMDeviceEnumerator enumerator;
        SetDefaultEndpointFn setDefaultEndpoint;
        string lastPlaybackId;

        delegate int SetDefaultEndpointFn(string deviceId, ERole role);

        public AudioEngine()
        {
            LoadRules();
        }

        // ---------- 配置 ----------
        void LoadRules()
        {
            rules.Clear();
            fallbackMicKey = null;
            RulesConfigured = false;

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            ConfigPath = Path.Combine(dir, "rules.txt");
            LogPath = Path.Combine(dir, "AutoMicSwitch.log");

            if (!File.Exists(ConfigPath)) return;

            try
            {
                string[] lines = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line.StartsWith("//")) continue;

                    // fallback = VID_XXXX  /  fallback = none
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "fallback") fallbackMicKey = (v.Length == 0 || v == "none") ? null : v;
                        continue;
                    }

                    // 播放设备关键字|目标麦克风关键字|说明
                    string[] parts = line.Split('|');
                    if (parts.Length < 2) continue;
                    string pk = parts[0].Trim();
                    string mk = parts[1].Trim();
                    if (pk.Length == 0 || mk.Length == 0) continue;
                    string note = parts.Length >= 3 ? parts[2].Trim() : "";
                    rules.Add(new Rule(pk, mk, note));
                }
                RulesConfigured = rules.Count > 0 || fallbackMicKey != null;
            }
            catch (Exception e)
            {
                rules.Clear();
                fallbackMicKey = null;
                Log("warn", "rules.txt 解析失败：" + e.Message);
            }
        }

        public List<Rule> GetRules()
        {
            lock (stateLock) { return new List<Rule>(rules); }
        }

        public string GetFallback()
        {
            lock (stateLock) { return fallbackMicKey; }
        }

        // ---------- 生命周期 ----------
        public void Start()
        {
            try { FileInfo fi = new FileInfo(LogPath); if (fi.Exists && fi.Length > 512 * 1024) fi.Delete(); }
            catch { }

            Log("info", "---- AutoMicSwitch 启动 ----");
            if (!RulesConfigured) Log("warn", "未找到有效的 rules.txt，当前没有任何联动规则（可从 rules.example.txt 复制）");
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            }
            catch (Exception e)
            {
                Log("err", "MMDevice 初始化失败: " + e.Message);
                return;
            }
            try
            {
                setDefaultEndpoint = CreatePolicyConfig();
            }
            catch (Exception e)
            {
                Log("err", "PolicyConfig 初始化失败: " + e.Message);
                return;
            }

            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "AudioEngine";
            thread.Start();
        }

        public void Stop()
        {
            stopped = true;
            Log("info", "引擎已停止");
        }

        public void Pause()
        {
            pausedFlag = true;
            lock (stateLock) { State.Paused = true; }
            Log("warn", "联动已暂停");
        }

        public void Resume()
        {
            pausedFlag = false;
            lock (stateLock) { State.Paused = false; }
            Log("ok", "联动已恢复");
            try { lastPlaybackId = null; } catch { }
        }

        public bool IsPaused { get { return pausedFlag; } }

        // 立即按当前默认播放设备重新应用一次规则
        public void ApplyNow()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    IMMDevice dev;
                    int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev);
                    if (hr != 0) { Log("warn", "当前无可用播放设备"); return; }
                    string pinst = GetProp(dev, FMT_INSTANCEID, 2);
                    Log("info", "手动同步：" + GetProp(dev, FMT_FRIENDLY, 14));
                    Apply(pinst);
                }
                catch (Exception e) { Log("err", "手动同步失败: " + e.Message); }
            });
        }

        // ---------- 主循环 ----------
        void Loop()
        {
            while (!stopped)
            {
                if (!pausedFlag)
                {
                    try { Poll(); }
                    catch (Exception e) { Log("warn", "轮询异常: " + e.Message); }
                }
                Thread.Sleep(PollIntervalMs);
            }
        }

        void Poll()
        {
            IMMDevice dev;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev);
            if (hr != 0) return;
            string id; dev.GetId(out id);

            if (id == lastPlaybackId) { RefreshCurrent(); return; }
            lastPlaybackId = id;

            string pname = GetProp(dev, FMT_FRIENDLY, 14);
            if (pname == null || pname.Length == 0 || pname[0] == '(') pname = GetProp(dev, FMT_DESC, 2);
            string pinst = GetProp(dev, FMT_INSTANCEID, 2);

            lock (stateLock)
            {
                State.PlaybackName = pname;
                State.PlaybackInstanceId = pinst;
                State.PlaybackVid = ExtractVid(pinst);
            }

            Log("info", "默认播放 → " + pname + "  [" + ExtractVid(pinst) + "]");
            Apply(pinst);
            RefreshCurrent();
        }

        void RefreshCurrent()
        {
            DeviceInfo p = GetDefault(Flow.Render, Role.Multimedia);
            DeviceInfo m = GetDefault(Flow.Capture, Role.Console);
            lock (stateLock)
            {
                if (p != null) { State.PlaybackName = p.Name; State.PlaybackInstanceId = p.InstanceId; State.PlaybackVid = p.Vid; }
                if (m != null) { State.MicName = m.Name; State.MicInstanceId = m.InstanceId; State.MicVid = m.Vid; }
            }
        }

        void Apply(string playbackInstance)
        {
            string micKey = null;
            string ruleText = null;
            bool isFallback = false;

            List<Rule> snapshot;
            string fb;
            lock (stateLock) { snapshot = new List<Rule>(rules); fb = fallbackMicKey; }

            foreach (Rule r in snapshot)
            {
                if (playbackInstance.IndexOf(r.PlaybackKey, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    micKey = r.MicKey;
                    ruleText = r.PlaybackKey + "  →  " + r.MicKey;
                    break;
                }
            }
            if (micKey == null)
            {
                if (string.IsNullOrEmpty(fb))
                {
                    lock (stateLock) { State.MatchedText = "该播放设备无规则，保持不变"; State.IsFallback = false; }
                    Log("warn", "   该播放设备无规则且未设兜底，保持不变");
                    return;
                }
                micKey = fb;
                ruleText = "兜底  →  " + fb;
                isFallback = true;
            }

            lock (stateLock)
            {
                State.MatchedText = ruleText;
                State.IsFallback = isFallback;
            }

            List<DeviceInfo> caps = Enumerate(Flow.Capture);
            DeviceInfo target = null;
            foreach (DeviceInfo d in caps)
            {
                if (d.InstanceId != null && d.InstanceId.IndexOf(micKey, StringComparison.OrdinalIgnoreCase) >= 0) { target = d; break; }
            }
            if (target == null)
            {
                lock (stateLock) { State.MicMatched = false; }
                Log("warn", "   找不到含 \"" + micKey + "\" 的麦克风，保持不变");
                return;
            }

            lock (stateLock) { State.MicMatched = true; State.Switches++; }
            SetDefaultMic(target.Id, target.InstanceId, target.Name);
        }

        void SetDefaultMic(string id, string inst, string name)
        {
            lock (setLock)
            {
                int hr1 = setDefaultEndpoint(id, ERole.eConsole);
                int hr2 = setDefaultEndpoint(id, ERole.eMultimedia);
                int hr3 = setDefaultEndpoint(id, ERole.eCommunications);
                Log("ok", "   默认麦克风 → " + name + "  [" + ExtractVid(inst) + "]  (hr=" + hr1 + "," + hr2 + "," + hr3 + ")");
            }
            lock (stateLock) { State.MicName = name; State.MicInstanceId = inst; State.MicVid = ExtractVid(inst); }

            // 后台读回校验，避免阻塞轮询
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < 3; i++)
                {
                    Thread.Sleep(2000);
                    string now = ReadBackMicId();
                    if (now == id)
                    {
                        Log("ok", "   +" + ((i + 1) * 2) + "s 读回一致，保持成功");
                    }
                    else
                    {
                        lock (stateLock) { State.Retries++; }
                        Log("warn", "   +" + ((i + 1) * 2) + "s 被改回，重试");
                        lock (setLock)
                        {
                            setDefaultEndpoint(id, ERole.eConsole);
                            setDefaultEndpoint(id, ERole.eMultimedia);
                            setDefaultEndpoint(id, ERole.eCommunications);
                        }
                    }
                }
            });
        }

        string ReadBackMicId()
        {
            IMMDevice back;
            int hrb = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out back);
            if (hrb != 0) return "(读取失败)";
            string bid; back.GetId(out bid);
            return bid;
        }

        // ---------- 设备枚举 ----------
        public DeviceInfo GetDefault(Flow flow, Role role)
        {
            try
            {
                IMMDevice dev;
                int hr = enumerator.GetDefaultAudioEndpoint((EDataFlow)flow, (ERole)role, out dev);
                if (hr != 0) return null;
                return ReadDevice(dev, flow, true);
            }
            catch { return null; }
        }

        public List<DeviceInfo> Enumerate(Flow flow)
        {
            List<DeviceInfo> list = new List<DeviceInfo>();
            try
            {
                IMMDeviceCollection col;
                int hr = enumerator.EnumAudioEndpoints((EDataFlow)flow, 1, out col); // 1 = DEVICE_STATE_ACTIVE
                if (hr != 0) return list;
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; col.Item(i, out d);
                    DeviceInfo di = ReadDevice(d, flow, false);
                    if (di != null) list.Add(di);
                }
            }
            catch { }
            return list;
        }

        DeviceInfo ReadDevice(IMMDevice d, Flow flow, bool isDefault)
        {
            DeviceInfo di = new DeviceInfo();
            di.FlowKind = flow;
            di.IsDefault = isDefault;
            string id; d.GetId(out id);
            di.Id = id;
            di.InstanceId = GetProp(d, FMT_INSTANCEID, 2);
            string name = GetProp(d, FMT_FRIENDLY, 14);
            if (name == null || name.Length == 0 || name[0] == '(') name = GetProp(d, FMT_DESC, 2);
            di.Name = name;
            return di;
        }

        // ---------- 日志 ----------
        public void Log(string level, string text)
        {
            LogEntry e = new LogEntry();
            e.Time = DateTime.Now;
            e.Level = level;
            e.Text = text;

            lock (logLock)
            {
                logBuffer.Add(e);
                if (logBuffer.Count > LogBufferMax) logBuffer.RemoveRange(0, logBuffer.Count - LogBufferMax);
            }
            lock (stateLock)
            {
                State.LastEventText = text;
                State.LastEventTime = e.Time;
                State.LastEventLevel = level;
                State.LastEventTick = State.LastEventTick + 1;
            }
            try
            {
                File.AppendAllText(LogPath, e.Time.ToString("yyyy-MM-dd HH:mm:ss ") + text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public List<LogEntry> SnapshotLog(int max)
        {
            lock (logLock)
            {
                int n = Math.Min(max, logBuffer.Count);
                return logBuffer.GetRange(logBuffer.Count - n, n);
            }
        }

        // ---------- 工具 ----------
        public static string ExtractVid(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "-";
            int i = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "-";
            int end = i + 4;
            while (end < instanceId.Length && end < i + 8 && IsHex(instanceId[end])) end++;
            return instanceId.Substring(i, end - i).ToUpperInvariant();
        }

        static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        const string FMT_FRIENDLY = "a45c254e-df1c-4efd-8020-67d146a850e0";
        const string FMT_DESC = "a45c254e-df1c-4efd-8020-67d146a850e0";
        const string FMT_INSTANCEID = "b3f8fa53-0004-438e-9003-51a46e139bfc";

        static string GetProp(IMMDevice d, string fmtid, uint pid)
        {
            try
            {
                IPropertyStore store;
                int hr = d.OpenPropertyStore(0, out store);
                if (hr != 0) return "(open err " + hr + ")";
                PROPERTYKEY k; k.fmtid = new Guid(fmtid); k.pid = pid;
                PropVariant v;
                hr = store.GetValue(ref k, out v);
                if (hr != 0) return "(get err " + hr + ")";
                if (v.vt == 31 && v.pointer != IntPtr.Zero) return Marshal.PtrToStringUni(v.pointer);
                return "(empty)";
            }
            catch (Exception e) { return "(err " + e.Message + ")"; }
        }

        SetDefaultEndpointFn CreatePolicyConfig()
        {
            Guid[] clsids = new Guid[]
            {
                new Guid("294935CE-F637-4E7C-A41B-AB255460B862"),
                new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"),
            };
            Guid iidV1 = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
            Guid iidV10 = new Guid("ca286fc3-91fd-42c3-8e9b-caffa66242e3");
            Guid iidVista = new Guid("568b9108-44bf-40b4-9006-86afe5b5a620");

            string err = "";
            foreach (Guid clsidValue in clsids)
            {
                Guid clsid = clsidValue;
                object obj;
                try { obj = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)); }
                catch (Exception e) { err += clsid + " 创建失败: " + e.Message + "; "; continue; }

                IPolicyConfigVista vista = TryQI<IPolicyConfigVista>(obj, iidVista);
                if (vista != null) { Log("ok", "PolicyConfig 就绪 (IPolicyConfigVista, CLSID " + clsid + ")"); return new SetDefaultEndpointFn(vista.SetDefaultEndpoint); }

                IPolicyConfig cfg = TryQI<IPolicyConfig>(obj, iidV10);
                if (cfg == null) cfg = TryQI<IPolicyConfig>(obj, iidV1);
                if (cfg != null) { Log("ok", "PolicyConfig 就绪 (IPolicyConfig, CLSID " + clsid + ")"); return new SetDefaultEndpointFn(cfg.SetDefaultEndpoint); }

                err += clsid + " 不支持已知接口; ";
            }
            throw new Exception(err);
        }

        static T TryQI<T>(object obj, Guid iid) where T : class
        {
            IntPtr punk = Marshal.GetIUnknownForObject(obj);
            IntPtr pItf = IntPtr.Zero;
            int hr = Marshal.QueryInterface(punk, ref iid, out pItf);
            Marshal.Release(punk);
            if (hr != 0) return null;
            Marshal.Release(pItf);
            return (T)obj;
        }
    }

    // ===================== COM 互操作 =====================
    enum EDataFlow { eRender = 0, eCapture = 1 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PropVariant value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // 未公开接口，Windows 7~11 通用，仅使用 SetDefaultEndpoint
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriodOut);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }

    // Vista 版接口：比上面少一个 ResetDeviceFormat，后续方法槽位前移
    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriodOut);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointer;
    }
}
