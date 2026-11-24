// SPDX-License-Identifier: MIT
// Minimal JSON parser: objects -> Dictionary<string, object>, arrays -> List<object>,
// numbers -> double, plus string / bool / null. Enough for glTF headers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SMCAD.Editor
{
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': i += 4; return true;
                case 'f': i += 5; return false;
                case 'n': i += 4; return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                i++; // ':'
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++; // '}'
                return d;
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++; // ']'
                return l;
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            i++; // closing quote
            return sb.ToString();
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---- helpers ----
        public static Dictionary<string, object> Obj(object o, string key)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v)
                ? v as Dictionary<string, object> : null;

        public static List<object> Arr(object o, string key)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v)
                ? v as List<object> : null;

        public static string Str(object o, string key, string def = null)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v) && v is string str
                ? str : def;

        public static double Num(object o, string key, double def = 0)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v) && v is double d
                ? d : def;

        public static int Int(object o, string key, int def = -1)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v) && v is double d
                ? (int)d : def;

        public static bool Bool(object o, string key, bool def = false)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v) && v is bool b
                ? b : def;

        public static bool Has(object o, string key)
            => (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).ContainsKey(key);
    }
}
