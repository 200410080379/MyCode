using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace MiniLink
{
    /// <summary>
    /// 轻量JSON序列化/反序列化工具
    /// 不依赖Newtonsoft.Json，减少包体大小
    /// 基于 Facebook MiniJSON 修改
    /// </summary>
    public static class MiniJson
    {
        /// <summary>
        /// 将对象序列化为JSON字符串
        /// 支持：Dictionary, List, string, int, float, bool, null
        /// </summary>
        public static string Serialize(object obj)
        {
            return _Serialize(obj);
        }

        private static string _Serialize(object obj)
        {
            if (obj == null) return "null";

            if (obj is string s) return EscapeString(s);

            if (obj is bool b) return b ? "true" : "false";

            if (obj is int i) return i.ToString();

            if (obj is long l) return l.ToString();

            if (obj is float f) return f.ToString("R");

            if (obj is double d) return d.ToString("R");

            if (obj is IDictionary dict)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(EscapeString(entry.Key.ToString()));
                    sb.Append(':');
                    sb.Append(_Serialize(entry.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }

            if (obj is IList list)
            {
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(_Serialize(item));
                }
                sb.Append(']');
                return sb.ToString();
            }

            // 枚举
            if (obj is Enum e) return ((int)(object)e).ToString();

            // 其他类型转string
            return EscapeString(obj.ToString());
        }

        private static string EscapeString(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append($"\\u{(int)c:x4}");
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 反序列化JSON字符串
        /// </summary>
        public static object Deserialize(string json)
        {
            var parser = new Parser(json);
            return parser.Parse();
        }

        private sealed class Parser
        {
            private const string WHITE_SPACE = " \t\n\r";
            private const string WORD_BREAK = " \t\n\r{}[],:\"";

            private readonly string json;
            private int idx;

            public Parser(string jsonString)
            {
                json = jsonString;
                idx = 0;
            }

            public object Parse()
            {
                return ParseValue();
            }

            private object ParseValue()
            {
                EatWhitespace();
                if (idx >= json.Length) return null;

                char c = json[idx];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (char.IsDigit(c) || c == '-') return ParseNumber();
                if (c == 't' || c == 'f') return ParseBool();
                if (c == 'n') return ParseNull();

                return null;
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                idx++; // skip '{'
                EatWhitespace();

                while (idx < json.Length && json[idx] != '}')
                {
                    EatWhitespace();
                    string key = ParseString();
                    EatWhitespace();
                    idx++; // skip ':'
                    object value = ParseValue();
                    dict[key] = value;

                    EatWhitespace();
                    if (idx < json.Length && json[idx] == ',') idx++;
                    EatWhitespace();
                }

                if (idx < json.Length) idx++; // skip '}'
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                idx++; // skip '['
                EatWhitespace();

                while (idx < json.Length && json[idx] != ']')
                {
                    list.Add(ParseValue());
                    EatWhitespace();
                    if (idx < json.Length && json[idx] == ',') idx++;
                    EatWhitespace();
                }

                if (idx < json.Length) idx++; // skip ']'
                return list;
            }

            private string ParseString()
            {
                if (json[idx] != '"') return null;
                idx++; // skip opening '"'

                var sb = new StringBuilder();
                while (idx < json.Length && json[idx] != '"')
                {
                    char c = json[idx++];
                    if (c == '\\')
                    {
                        if (idx >= json.Length) break;
                        char esc = json[idx++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                string hex = json.Substring(idx, 4);
                                sb.Append((char)Convert.ToInt32(hex, 16));
                                idx += 4;
                                break;
                            default: sb.Append(esc); break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                if (idx < json.Length) idx++; // skip closing '"'
                return sb.ToString();
            }

            private object ParseNumber()
            {
                EatWhitespace();
                int start = idx;

                if (json[idx] == '-') idx++;

                while (idx < json.Length && char.IsDigit(json[idx])) idx++;

                if (idx < json.Length && json[idx] == '.')
                {
                    idx++;
                    while (idx < json.Length && char.IsDigit(json[idx])) idx++;
                }

                if (idx < json.Length && (json[idx] == 'e' || json[idx] == 'E'))
                {
                    idx++;
                    if (idx < json.Length && (json[idx] == '+' || json[idx] == '-')) idx++;
                    while (idx < json.Length && char.IsDigit(json[idx])) idx++;
                }

                string numStr = json.Substring(start, idx - start);

                if (long.TryParse(numStr, out long l) && l >= int.MinValue && l <= int.MaxValue)
                    return (int)l;

                if (double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                {
                    if (d >= float.MinValue && d <= float.MaxValue)
                        return (float)d;
                    return d;
                }

                return 0;
            }

            private bool ParseBool()
            {
                if (json[idx] == 't')
                {
                    idx += 4; // skip "true"
                    return true;
                }
                idx += 5; // skip "false"
                return false;
            }

            private object ParseNull()
            {
                idx += 4; // skip "null"
                return null;
            }

            private void EatWhitespace()
            {
                while (idx < json.Length && WHITE_SPACE.IndexOf(json[idx]) >= 0)
                    idx++;
            }
        }
    }
}
