using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Writes the object graph MiniJson produces (Dictionary / List / string / long / double / bool / null)
    /// back to JSON, so vpm-manifest.json can be edited without losing keys the Manager does not know.
    /// </summary>
    internal static class JsonWriter
    {
        public static string Write(Dictionary<string, object> root, string newline)
        {
            var sb = new StringBuilder();
            WriteValue(sb, root, 0, newline);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int depth, string newline)
        {
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{').Append(newline);
                var first = true;
                foreach (var pair in dict)
                {
                    if (!first) sb.Append(',').Append(newline);
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, pair.Key);
                    sb.Append(": ");
                    WriteValue(sb, pair.Value, depth + 1, newline);
                }
                sb.Append(newline);
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var list = value as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[').Append(newline);
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',').Append(newline);
                    Indent(sb, depth + 1);
                    WriteValue(sb, list[i], depth + 1, newline);
                }
                sb.Append(newline);
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            var text = value as string;
            if (text != null) { WriteString(sb, text); return; }
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            throw new InvalidOperationException("Unsupported JSON value type " + value.GetType().Name);
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
