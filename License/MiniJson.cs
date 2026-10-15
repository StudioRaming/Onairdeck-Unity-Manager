using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OnAirDeck.License
{
    /// <summary>
    /// Minimal JSON reader for the license payload and the session marker. Keeps the assembly free of
    /// UnityEngine (JsonUtility) and third-party JSON so it compiles anywhere and can be merged into
    /// a seller's protected assembly later.
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>
        /// Parses one JSON object. Strings are unescaped, integers become long, other numbers double,
        /// nested objects/arrays become Dictionary/List. Returns null when the text is not one JSON object.
        /// </summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            if (json == null) return null;
            var parser = new Parser(json);
            try
            {
                parser.SkipWhitespace();
                var obj = parser.ReadValue() as Dictionary<string, object>;
                parser.SkipWhitespace();
                return parser.AtEnd ? obj : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static string GetString(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
        }

        public static bool TryGetInt(Dictionary<string, object> obj, string key, out long value)
        {
            value = 0;
            object raw;
            if (obj == null || !obj.TryGetValue(key, out raw) || !(raw is long)) return false;
            value = (long)raw;
            return true;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }

            public bool AtEnd { get { return _i >= _s.Length; } }

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new FormatException();
                return _s[_i];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException();
                _i++;
            }

            public object ReadValue()
            {
                SkipWhitespace();
                var c = Peek();
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't') { ReadLiteral("true"); return true; }
                if (c == 'f') { ReadLiteral("false"); return false; }
                if (c == 'n') { ReadLiteral("null"); return null; }
                return ReadNumber();
            }

            private Dictionary<string, object> ReadObject()
            {
                Expect('{');
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Peek() == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWhitespace();
                    var key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    obj[key] = ReadValue();
                    SkipWhitespace();
                    var c = Peek();
                    _i++;
                    if (c == '}') return obj;
                    if (c != ',') throw new FormatException();
                }
            }

            private List<object> ReadArray()
            {
                Expect('[');
                var list = new List<object>();
                SkipWhitespace();
                if (Peek() == ']') { _i++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    var c = Peek();
                    _i++;
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException();
                }
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Peek();
                    _i++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = Peek();
                    _i++;
                    switch (e)
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
                            if (_i + 4 > _s.Length) throw new FormatException();
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
            }

            private void ReadLiteral(string literal)
            {
                if (_i + literal.Length > _s.Length || string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0) throw new FormatException();
                _i += literal.Length;
            }

            private object ReadNumber()
            {
                var start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                if (_i == start) throw new FormatException();
                var text = _s.Substring(start, _i - start);
                long l;
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException();
            }
        }
    }
}
