// Json.cs — 极简 JSON 工具
//
// 出站：把状态拼成 JSON 发给界面。入站：从界面发来的消息里取字段。
// 手写不引依赖，够用就行——但要正确处理 \" 这类转义，设备名里带引号不能把解析带偏。
//
// 作者：沐辉 · MIT License
using System;
using System.Globalization;
using System.Text;

namespace AutoMicSwitch
{
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

        public static string Num(long n) { return n.ToString(CultureInfo.InvariantCulture); }

        // 定位某个键对应的值的起始位置。键必须真的处在成员位置上：
        // 前面最近的非空白字符是 { 或 ,，后面紧跟冒号。这样值里出现同名字符串不会被误认。
        static int ValueStart(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return -1;

            string pat = "\"" + key + "\"";
            int from = 0;
            while (from < json.Length)
            {
                int i = json.IndexOf(pat, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;

                int j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(json[j])) j--;
                if (j < 0 || json[j] == '{' || json[j] == ',')
                {
                    int c = i + pat.Length;
                    while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
                    if (c < json.Length && json[c] == ':')
                    {
                        int v = c + 1;
                        while (v < json.Length && char.IsWhiteSpace(json[v])) v++;
                        return v;
                    }
                }
                from = i + pat.Length;
            }
            return -1;
        }

        // 取字符串字段，找不到返回空串
        public static string Pick(string json, string key)
        {
            int p = ValueStart(json, key);
            if (p < 0 || p >= json.Length || json[p] != '"') return "";

            StringBuilder sb = new StringBuilder();
            for (int i = p + 1; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else if (ch == '"') break;
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        // 取布尔字段。值写成字符串（"true"）也认，取不到就返回 def
        public static bool PickBool(string json, string key, bool def)
        {
            int p = ValueStart(json, key);
            if (p < 0 || p >= json.Length) return def;

            if (json[p] == '"')
            {
                string s = Pick(json, key);
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return def;
            }
            if (string.Compare(json, p, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0) return true;
            if (string.Compare(json, p, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) return false;
            return def;
        }

        // 取数字字段，非数字返回 def
        public static long PickLong(string json, string key, long def)
        {
            int p = ValueStart(json, key);
            if (p < 0 || p >= json.Length) return def;

            int end = p;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '+')) end++;
            if (end == p) return def;

            long v;
            if (long.TryParse(json.Substring(p, end - p), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            return def;
        }
    }
}
