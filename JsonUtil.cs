using System;
using System.Collections.Generic;
using System.Text;

namespace RevitKJChecklist
{
    internal static class JsonUtil
    {
        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string BuildInit(Dictionary<string, string> meta,
                                       Dictionary<string, List<string>> marks)
        {
            var sb = new StringBuilder();
            sb.Append("{\"meta\":{");
            string[] keys = { "pname", "pcode", "pblock", "porg", "pstage", "plead", "pdate", "psw", "psheets" };
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) sb.Append(',');
                meta.TryGetValue(keys[i], out var v);
                sb.Append(Esc(keys[i])).Append(':').Append(Esc(v ?? ""));
            }
            sb.Append(",\"pexec\":[]},\"marks\":{");

            bool first = true;
            foreach (var kv in marks)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(Esc(kv.Key)).Append(":[");
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Esc(kv.Value[i]));
                }
                sb.Append(']');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Unquote(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (raw.Length < 2 || raw[0] != '"' || raw[raw.Length - 1] != '"') return raw;

            var sb = new StringBuilder(raw.Length);
            for (int i = 1; i < raw.Length - 1; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length - 1)
                {
                    char n = raw[++i];
                    switch (n)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
