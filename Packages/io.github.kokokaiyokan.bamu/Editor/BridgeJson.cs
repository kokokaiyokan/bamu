using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace bamu
{
    // Small dependency-free JSON codec. Keeps Python's object-shaped state intact,
    // including unknown fields, without calling Unity APIs from HTTP worker threads.
    internal static class BridgeJson
    {
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            var value = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < pairs.Length; i += 2) value.Add((string)pairs[i], pairs[i + 1]);
            return value;
        }
        public static Dictionary<string, object> Map(object value) => value as Dictionary<string, object> ?? throw new InvalidDataException("JSONオブジェクトが必要です。");
        public static object Value(Dictionary<string, object> value, string key, object fallback = null) => value.TryGetValue(key, out var result) ? result : fallback;
        public static string Str(Dictionary<string, object> value, string key, string fallback = "") => Convert.ToString(Value(value, key, fallback), CultureInfo.InvariantCulture) ?? fallback;
        public static double Num(Dictionary<string, object> value, string key, double fallback = 0) => value.TryGetValue(key, out var result) && result != null ? Convert.ToDouble(result, CultureInfo.InvariantCulture) : fallback;
        public static bool Bool(Dictionary<string, object> value, string key, bool fallback = false) => value.TryGetValue(key, out var result) ? result is bool flag && flag : fallback;
        public static List<object> List(object value) => value is List<object> list ? list :
            value is IEnumerable sequence && !(value is string) && !(value is IDictionary) ? sequence.Cast<object>().ToList() : throw new InvalidDataException("JSON配列が必要です。");
        public static object Parse(string json) => new Parser(json).Read();
        public static Dictionary<string, object> Copy(Dictionary<string, object> value) => Map(Parse(Stringify(value)));
        public static string Stringify(object value)
        {
            var output = new StringBuilder(); Write(output, value); return output.ToString();
        }
        private static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string text)
            {
                output.Append('"');
                foreach (var c in text)
                {
                    switch (c)
                    {
                        case '"': output.Append("\\\""); break;
                        case '\\': output.Append("\\\\"); break;
                        case '\n': output.Append("\\n"); break;
                        case '\r': output.Append("\\r"); break;
                        case '\t': output.Append("\\t"); break;
                        default: if (c < 32) output.Append("\\u" + ((int)c).ToString("x4")); else output.Append(c); break;
                    }
                }
                output.Append('"'); return;
            }
            if (value is bool flag) { output.Append(flag ? "true" : "false"); return; }
            if (value is IDictionary<string, object> map)
            {
                output.Append('{'); var first = true;
                foreach (var pair in map)
                {
                    if (!first) output.Append(','); first = false;
                    Write(output, pair.Key); output.Append(':'); Write(output, pair.Value);
                }
                output.Append('}'); return;
            }
            if (value is IEnumerable sequence)
            {
                output.Append('['); var first = true;
                foreach (var item in sequence) { if (!first) output.Append(','); first = false; Write(output, item); }
                output.Append(']'); return;
            }
            if (!(value is IFormattable number)) throw new InvalidDataException("未対応のJSON値です。");
            var numeric = number.ToString(null, CultureInfo.InvariantCulture);
            if (numeric == "NaN" || numeric.Contains("Infinity")) throw new InvalidDataException("不正な数値です。");
            output.Append(numeric);
        }
        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                { writer.Write(Stringify(value)); writer.Flush(); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class Parser
        {
            private readonly string json;
            private int offset;
            public Parser(string json) { this.json = json ?? ""; }
            private void Space() { while (offset < json.Length && " \r\n\t".IndexOf(json[offset]) >= 0) offset++; }
            private char Peek => offset < json.Length ? json[offset] : '\0';
            private void Expect(char c) { if (Peek != c) throw new InvalidDataException("JSONの形式が不正です。"); offset++; }
            public object Read() { var value = ReadValue(0); Space(); if (offset != json.Length) throw new InvalidDataException("JSONの末尾が不正です。"); return value; }
            private object ReadValue(int depth)
            {
                if (depth > 64) throw new InvalidDataException("JSONの階層が深すぎます。");
                Space();
                if (Peek == '"') return ReadString();
                if (Peek == '{')
                {
                    offset++; Space(); var map = Obj();
                    if (Peek == '}') { offset++; return map; }
                    while (true)
                    {
                        Space(); var key = ReadString(); Space(); Expect(':');
                        if (map.ContainsKey(key)) throw new InvalidDataException("JSONキーが重複しています。");
                        map.Add(key, ReadValue(depth + 1)); Space();
                        if (Peek == '}') { offset++; return map; } Expect(',');
                    }
                }
                if (Peek == '[')
                {
                    offset++; Space(); var list = new List<object>();
                    if (Peek == ']') { offset++; return list; }
                    while (true) { list.Add(ReadValue(depth + 1)); Space(); if (Peek == ']') { offset++; return list; } Expect(','); }
                }
                foreach (var literal in new[] { "true", "false", "null" })
                    if (json.Length - offset >= literal.Length && string.CompareOrdinal(json, offset, literal, 0, literal.Length) == 0)
                    { offset += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
                var start = offset;
                if (Peek == '-') offset++;
                if (Peek == '0') offset++;
                else { if (Peek < '1' || Peek > '9') throw new InvalidDataException("JSON値が不正です。"); while (Peek >= '0' && Peek <= '9') offset++; }
                if (Peek == '.') { offset++; Digits(); }
                if (Peek == 'e' || Peek == 'E') { offset++; if (Peek == '+' || Peek == '-') offset++; Digits(); }
                var token = json.Substring(start, offset - start);
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsInfinity(number) || double.IsNaN(number)) throw new InvalidDataException("JSON数値が不正です。");
                return number;
            }
            private void Digits() { var start = offset; while (Peek >= '0' && Peek <= '9') offset++; if (start == offset) throw new InvalidDataException("JSON数値が不正です。"); }
            private string ReadString()
            {
                Expect('"'); var output = new StringBuilder();
                while (offset < json.Length)
                {
                    var c = json[offset++];
                    if (c == '"') return output.ToString();
                    if (c < 32) throw new InvalidDataException("JSON文字列が不正です。");
                    if (c != '\\') { output.Append(c); continue; }
                    if (offset >= json.Length) break;
                    c = json[offset++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': output.Append(c); break;
                        case 'b': output.Append('\b'); break; case 'f': output.Append('\f'); break;
                        case 'n': output.Append('\n'); break; case 'r': output.Append('\r'); break; case 't': output.Append('\t'); break;
                        case 'u':
                            if (offset + 4 > json.Length || !ushort.TryParse(json.Substring(offset, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new InvalidDataException("Unicodeエスケープが不正です。");
                            output.Append((char)code); offset += 4; break;
                        default: throw new InvalidDataException("JSONエスケープが不正です。");
                    }
                }
                throw new InvalidDataException("JSON文字列が閉じられていません。");
            }
        }
    }
}
