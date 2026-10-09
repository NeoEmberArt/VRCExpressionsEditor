using System.Collections.Generic;
using System.Linq;
using System.Text;

// ═══════════════════════════════════════════════════════════════════════════════
// MINI JSON — dependency-free reader + pretty writer
//
// Works over plain Dictionary<string,object> / List<object> / primitives so the
// debug logger, snapshot store and structure export can all build and consume
// arbitrary nested structures without JsonUtility's no-Dictionary limitation.
// ═══════════════════════════════════════════════════════════════════════════════

internal static class MiniJson
{
    // ── WRITE ─────────────────────────────────────────────────────────────────

    public static string Pretty(object obj) => Write(obj, 0);

    static string Write(object obj, int indent)
    {
        string pad   = new string(' ', indent * 2);
        string padIn = new string(' ', (indent + 1) * 2);

        switch (obj)
        {
            case null:
                return "null";
            case bool b:
                return b ? "true" : "false";
            case string s:
                return "\"" + Escape(s) + "\"";
            case System.Collections.IDictionary dict:
            {
                if (dict.Count == 0) return "{}";
                var sb = new StringBuilder();
                sb.Append("{\n");
                int i = 0, count = dict.Count;
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    sb.Append(padIn).Append('"').Append(Escape(kv.Key?.ToString() ?? "")).Append("\": ");
                    sb.Append(Write(kv.Value, indent + 1));
                    i++;
                    if (i < count) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append(pad).Append("}");
                return sb.ToString();
            }
            case System.Collections.IEnumerable en when !(obj is string):
            {
                var items = en.Cast<object>().ToList();
                if (items.Count == 0) return "[]";
                var sb = new StringBuilder();
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(padIn).Append(Write(items[i], indent + 1));
                    if (i < items.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append(pad).Append("]");
                return sb.ToString();
            }
            case float f:
                return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case double d:
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case int _:
            case long _:
            case short _:
            case byte _:
                return obj.ToString();
            default:
                return "\"" + Escape(obj.ToString()) + "\"";
        }
    }

    static string Escape(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "")
         .Replace("\n", "\\n")
         .Replace("\t", "\\t");

    // ── READ ──────────────────────────────────────────────────────────────────

    /// Returns Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null.
    /// Throws on malformed input — callers wrap in try/catch.
    public static object Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int i = 0;
        var result = ParseValue(json, ref i);
        return result;
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new System.Exception("Unexpected end of JSON");
        char c = s[i];
        if (c == '{') return ParseObject(s, ref i);
        if (c == '[') return ParseArray(s, ref i);
        if (c == '"') return ParseString(s, ref i);
        if (c == 't' && s.Length >= i + 4 && s.Substring(i, 4) == "true")  { i += 4; return true; }
        if (c == 'f' && s.Length >= i + 5 && s.Substring(i, 5) == "false") { i += 5; return false; }
        if (c == 'n' && s.Length >= i + 4 && s.Substring(i, 4) == "null")  { i += 4; return null; }
        return ParseNumber(s, ref i);
    }

    static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var d = new Dictionary<string, object>();
        i++; // {
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return d; }
        while (i < s.Length)
        {
            SkipWs(s, ref i);
            string key = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new System.Exception("Expected ':' in JSON object");
            i++;
            d[key] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return d; }
            throw new System.Exception("Malformed JSON object");
        }
        throw new System.Exception("Unterminated JSON object");
    }

    static List<object> ParseArray(string s, ref int i)
    {
        var list = new List<object>();
        i++; // [
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }
        while (i < s.Length)
        {
            list.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return list; }
            throw new System.Exception("Malformed JSON array");
        }
        throw new System.Exception("Unterminated JSON array");
    }

    static string ParseString(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '"') throw new System.Exception("Expected string in JSON");
        i++;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
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
                        if (i + 4 <= s.Length)
                        {
                            string hex = s.Substring(i, 4); i += 4;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                             System.Globalization.CultureInfo.InvariantCulture, out int code))
                                sb.Append((char)code);
                        }
                        break;
                    default: sb.Append(e); break;
                }
                continue;
            }
            sb.Append(c);
        }
        throw new System.Exception("Unterminated JSON string");
    }

    static object ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' ||
                                s[i] == 'e' || s[i] == 'E')) i++;
        string num = s.Substring(start, i - start);
        if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        throw new System.Exception("Malformed JSON number '" + num + "'");
    }

    // ── typed helpers for readers ─────────────────────────────────────────────

    public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;
    public static List<object> AsList(object o) => o as List<object>;

    public static string Str(Dictionary<string, object> d, string key, string fallback = "")
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        return v as string ?? v.ToString();
    }

    public static bool Bool(Dictionary<string, object> d, string key, bool fallback = false)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        if (v is bool b) return b;
        if (v is double dd) return dd != 0d;
        return fallback;
    }

    public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        if (v is int i) return i;
        if (v is long l && l >= int.MinValue && l <= int.MaxValue) return (int)l;
        if (v is double dd) return (int)dd;
        if (v is bool b) return b ? 1 : 0;
        if (v is string s && int.TryParse(s, out int r)) return r;
        return fallback;
    }

    public static float Float(Dictionary<string, object> d, string key, float fallback = 0f)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        if (v is float f) return f;
        if (v is int i) return i;
        if (v is long l) return l;
        if (v is double dd) return (float)dd;
        if (v is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out float r)) return r;
        return fallback;
    }

    public static List<object> List(Dictionary<string, object> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out var v)) return new List<object>();
        return v as List<object> ?? new List<object>();
    }

    public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out var v)) return null;
        return v as Dictionary<string, object>;
    }
}
