using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // Only layouts checked against a platform export receive field names.
    // A different event, subtype, arity or field type retains the entire parsed tree.
    internal static class StructuredEventData
    {
        public static bool ShouldParse(BracketsNode node)
        {
            if (List(node, 1) && Literal(node[0], "U")) return false;
            if (!List(node, 2) || !node[0].IsValueNode || !node[1].IsValueNode) return true;
            switch (node[0].Text)
            {
                case "S":
                case "N":
                case "D":
                case "R": return false;
                case "B": return !Literal(node[1], "0") && !Literal(node[1], "1");
                default: return true;
            }
        }

        public static string Parse(string eventCode, BracketsNode node)
        {
            var value = KnownData(eventCode, node) ?? new Dictionary<string, object>
            {
                ["$format"] = "1c-brackets-v1",
                ["nodes"] = TokenTree(node)
            };
            var json = new StringBuilder();
            WriteJson(json, value);
            return json.ToString();
        }

        private static object KnownData(string eventCode, BracketsNode node)
        {
            if (!List(node, 2) || !Literal(node[0], "P") || node[1].IsValueNode) return null;
            var body = node[1];
            switch (eventCode)
            {
                case "_$Session$_.Authentication":
                    if (List(body, 4) && Literal(body[0], "518") && Typed(body[1], "S") &&
                        Typed(body[2], "S") && Typed(body[3], "N"))
                        return new Dictionary<string, object>
                        {
                            ["Name"] = Text(body[1]),
                            ["CurrentOSUser"] = Text(body[2]),
                            ["AuthenticationMethod"] = body[3][1].Text
                        };
                    break;
                case "_$InfoBase$_.ConfigUpdate":
                case "_$InfoBase$_.DBConfigUpdate":
                    // This layout has no numeric subtype before the two values.
                    if (List(body, 2) && Typed(body[0], "S") && Typed(body[1], "S"))
                        return new Dictionary<string, object>
                        {
                            ["Vendor"] = Text(body[0]), ["Version"] = Text(body[1])
                        };
                    break;
                case "_$InfoBase$_.ExclusiveModeChange":
                    if (List(body, 2) && Literal(body[0], "2") && Typed(body[1], "B") &&
                        (Literal(body[1][1], "0") || Literal(body[1][1], "1")))
                        return new Dictionary<string, object>
                        {
                            ["ExclusiveMode"] = Literal(body[1][1], "1") ? "true" : "false"
                        };
                    break;
                case "_$Access$_.Access":
                    if (List(body, 2) && Literal(body[0], "1"))
                    {
                        var table = AccessTable(body[1]);
                        if (table != null) return new Dictionary<string, object> { ["Data"] = table };
                    }
                    break;
            }
            return null;
        }

        private static object AccessTable(BracketsNode node)
        {
            if (!List(node, 2) || !Literal(node[0], "Z")) return null;
            var table = node[1];
            // Verified layout: two columns with Pattern descriptors, one row of strings.
            // Other table layouts remain available as tokens instead of being guessed.
            if (!List(table, 5) || !Literal(table[0], "2") || !Literal(table[1], "1") ||
                !List(table[4], 2) || !Typed(table[4][0], "S") || !Typed(table[4][1], "S")) return null;
            var columns = new List<object>();
            var row = new Dictionary<string, object>();
            for (var i = 0; i < 2; i++)
            {
                var column = table[i + 2];
                if (!List(column, 2) || !column[0].IsValueNode || !List(column[1], 1) ||
                    !Literal(column[1][0], "Pattern")) return null;
                var name = column[0].Text.Replace("\"\"", "\"");
                if (row.ContainsKey(name)) return null;
                columns.Add(new Dictionary<string, object> { ["name"] = name });
                row.Add(name, Text(table[4][i]));
            }
            return new Dictionary<string, object> { ["Column"] = columns, ["Row"] = row };
        }

        private static bool List(BracketsNode node, int count) => !node.IsValueNode && node.Count == count;
        private static bool Literal(BracketsNode node, string value) => node.IsValueNode && node.Text == value;
        private static bool Typed(BracketsNode node, string type) =>
            List(node, 2) && Literal(node[0], type) && node[1].IsValueNode;
        private static string Text(BracketsNode node) => node[1].Text.Replace("\"\"", "\"");

        private static object TokenTree(BracketsNode node)
        {
            // Keep raw leaf text, including doubled quotes and exact numeric tokens.
            if (node.IsValueNode) return node.Text;
            var values = new List<object>(node.Count);
            foreach (var child in node) values.Add(TokenTree(child));
            return values;
        }

        // The contract contains only objects, arrays and strings. No numeric conversion
        // and no new JSON dependency in the netstandard2.1 production library.
        private static void WriteJson(StringBuilder json, object value)
        {
            if (value is string text)
            {
                WriteString(json, text);
            }
            else if (value is Dictionary<string, object> properties)
            {
                json.Append('{');
                var first = true;
                foreach (var property in properties)
                {
                    if (!first) json.Append(',');
                    first = false;
                    WriteString(json, property.Key);
                    json.Append(':');
                    WriteJson(json, property.Value);
                }
                json.Append('}');
            }
            else if (value is List<object> items)
            {
                json.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    WriteJson(json, items[i]);
                }
                json.Append(']');
            }
            else
            {
                throw new InvalidOperationException("Unsupported structured JSON value");
            }
        }

        private static void WriteString(StringBuilder json, string text)
        {
            json.Append('"');
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    default:
                        if (character < 0x20 || char.IsSurrogate(character))
                            json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(character);
                        break;
                }
            }
            json.Append('"');
        }
    }
}
