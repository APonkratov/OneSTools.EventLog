using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // Only layouts checked against a platform export or controlled platform writes receive field names.
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
            return Json(value);
        }

        private static object KnownData(string eventCode, BracketsNode node)
        {
            if (!List(node, 2) || !Literal(node[0], "P") || node[1].IsValueNode) return null;
            var body = node[1];
            var positional = PositionalEventData.Parse(eventCode, node);
            if (positional != null) return positional;
            switch (eventCode)
            {
                case "_$User$_.AuthenticationLock":
                case "_$User$_.AuthenticationUnlock":
                case "_$User$_.AuthenticationUnlockError":
                case "_$Session$_.PasswordRequirementsViolationOnAuthentication":
                    if (List(body, 2) && Literal(body[0], "1") && List(body[1], 2) &&
                        Literal(body[1][0], "Имя") && Typed(body[1][1], "S"))
                        return new Dictionary<string, object> { ["Name"] = Text(body[1][1]) };
                    break;
                case "_$Session$_.Authentication":
                case "_$Session$_.AuthenticationError":
                    if (List(body, 4) && Literal(body[0], "518") && Typed(body[1], "S") &&
                        Typed(body[2], "S") && Typed(body[3], "N"))
                        return new Dictionary<string, object>
                        {
                            ["Name"] = Text(body[1]),
                            ["CurrentOSUser"] = Text(body[2]),
                            ["AuthenticationMethod"] = body[3][1].Text
                        };
                    if (List(body, 3) && Literal(body[0], "514") && Typed(body[1], "S") && Typed(body[2], "N"))
                        return new Dictionary<string, object>
                        {
                            ["Name"] = Text(body[1]), ["AuthenticationMethod"] = body[2][1].Text
                        };
                    if (List(body, 3) && Literal(body[0], "513") && Typed(body[1], "S") && Typed(body[2], "N"))
                        return new Dictionary<string, object> { ["OSUser"] = Text(body[1]), ["AuthenticationMethod"] = body[2][1].Text };
                    break;
                case "_$InfoBase$_.ConfigUpdate":
                case "_$InfoBase$_.DBConfigUpdate":
                case "_$InfoBase$_.ConfigUpdateError":
                case "_$InfoBase$_.DBConfigUpdateError":
                    // This layout has no numeric subtype before the two values.
                    if (List(body, 2) && Typed(body[0], "S") && Typed(body[1], "S"))
                        return new Dictionary<string, object>
                        {
                            ["Vendor"] = Text(body[0]), ["Version"] = Text(body[1])
                        };
                    break;
                case "_$InfoBase$_.ExclusiveModeChange":
                case "_$InfoBase$_.ExclusiveModeChangeError":
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
                case "_$Access$_.AccessDenied":
                    if (List(body, 2) && Literal(body[0], "1") && Typed(body[1], "S"))
                        return new Dictionary<string, object> { ["Right"] = Text(body[1]) };
                    if (List(body, 3) && Literal(body[0], "6") && Typed(body[1], "S") &&
                        List(body[2], 1) && Literal(body[2][0], "U"))
                        return new Dictionary<string, object> { ["Action"] = Text(body[1]), ["Data"] = null };
                    break;
                case "_$Session$_.ConfigExtensionApplyError":
                case "_$InfoBase$_.UserPasswordPolicyDelete":
                case "_$User$_.Delete":
                    if (List(body, 1) && Typed(body[0], "S"))
                        return new Dictionary<string, object> { ["Name"] = Text(body[0]) };
                    break;
                case "_$InfoBase$_.SecondAuthenticationFactorTemplateNew":
                case "_$InfoBase$_.SecondAuthenticationFactorTemplateUpdate":
                case "_$InfoBase$_.SecondAuthenticationFactorTemplateDelete":
                    if (List(body, 2) && Literal(body[0], "1") && List(body[1], 2) &&
                        Literal(body[1][0], "Имя") && Typed(body[1][1], "S"))
                        return new Dictionary<string, object> { ["Name"] = Text(body[1][1]) };
                    break;
                case "_$InfoBase$_.RegionalSettingsChange":
                    return Fields(body, new[] { "LocaleCode", "UseCurrentSessionSettings", "NumbersDecimalSeparator",
                        "NumbersDigitGroupSeparator", "NumbersDigitGroupFormat", "NegativeNumberPresentation",
                        "BooleanFalsePresentation", "BooleanTruePresentation", "DatePresentationFormat",
                        "TimePresentationFormat", "FirstDayOfWeek" }, "SBSSSNSSSSN");
                case "_$Session$_.ExternalDataProcessorConnect":
                case "_$Session$_.ExternalDataProcessorConnectError":
                case "_$Session$_.ExternalReportConnect":
                case "_$Session$_.ExternalReportConnectError":
                    return Fields(body, new[] { "Path", "Name", "SafeMode", "SecurityProfile", "UnsafeOperationProtection", "LanguageCode" }, "SSBSBS");
                case "_$Session$_.AddInAttach":
                case "_$Session$_.AddInAttachError":
                    // Only the observed undefined Type/AttachmentType layout is verified.
                    return Fields(body, new[] { "Name", "Location", "Type", "AttachmentType" }, "SSUU");
                case "_$InfoBase$_.SessionLockChange":
                case "_$InfoBase$_.SessionLockChangeError":
                    return Fields(body, new[] { "Use", "Begin", "End", "Message", "KeyCode", "Parameter" }, "BDDSSS");
                case "_$InfoBase$_.ConfigExtensionUpdate":
                case "_$InfoBase$_.DBConfigExtensionUpdate":
                case "_$InfoBase$_.DBConfigExtensionUpdateError":
                case "_$InfoBase$_.DBConfigExtensionDelete":
                    return Fields(body, new[] { "Name", "Version", "Vendor", "UUID", "HashSum", "Synonym", "SafeMode", "SecurityProfile", "Active", "UnsafeOperationProtection", "Purpose", "UsedInDistributedInfoBase", "Scope", "DefaultRoles", "UseDefaultRolesForAllUsers" }, "SSSSSSBSBBSBSAB");
                case "_$InfoBase$_.PredefinedDataUpdate":
                case "_$Data$_.PredefinedDataInitialization":
                case "_$Data$_.UpdatePredefinedData":
                    if (List(body, 2) && Literal(body[0], "1") && List(body[1], 2) &&
                        (Literal(body[1][0], "Данные") || Literal(body[1][0], "Ссылка")))
                    {
                        var table = AccessTable(body[1][1]);
                        if (table != null) return new Dictionary<string, object> { ["Data"] = table };
                    }
                    break;
            }
            if (body.Count > 0 && Count(body[0], out var count) && count > 0 && (long)count + 1 == body.Count)
            {
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                for (var i = 1; i <= count; i++)
                {
                    var pair = body[i];
                    if (!List(pair, 2) || !pair[0].IsValueNode) return null;
                    var name = pair[0].Text.Replace("\"\"", "\"");
                    if (values.ContainsKey(name)) return null;
                    var field = NamedValue(pair[1]);
                    if (field == null) return null;
                    values.Add(name, field);
                }
                // Preserve the complete wire evidence as well as named values. Reference
                // identity is retained without claiming that an object currently exists.
                return new Dictionary<string, object>
                {
                    ["$format"] = "1c-named-data-v1", ["values"] = values, ["nodes"] = TokenTree(node)
                };
            }
            return null;
        }

        private static object NamedValue(BracketsNode node)
        {
            if (List(node, 1) && Literal(node[0], "U")) return new Dictionary<string, object> { ["$type"] = "U" };
            if (node.IsValueNode || node.Count != 2 || !node[0].IsValueNode || !node[1].IsValueNode) return null;
            switch (node[0].Text)
            {
                case "S": return Text(node);
                case "N": return node[1].Text;
                case "B": return Literal(node[1], "1") ? "true" : Literal(node[1], "0") ? "false" : null;
                case "D":
                    return DateTime.TryParseExact(node[1].Text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null;
                case "@":
                case "R": return new Dictionary<string, object> { ["$type"] = node[0].Text, ["value"] = node[1].Text };
                default: return null;
            }
        }

        private static object AccessTable(BracketsNode node)
        {
            if (!List(node, 2) || !Literal(node[0], "Z")) return null;
            var table = node[1];
            if (table.IsValueNode || table.Count < 2 || !Count(table[0], out var columnCount) ||
                !Count(table[1], out var rowCount) || columnCount == 0 ||
                (long)columnCount + rowCount + 2 != table.Count) return null;
            var columns = new List<object>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var orderedNames = new List<string>();
            for (var i = 0; i < columnCount; i++)
            {
                var column = table[i + 2];
                if (!List(column, 2) || !column[0].IsValueNode) return null;
                var pattern = column[1];
                if (!(List(pattern, 1) && Literal(pattern[0], "Pattern")) &&
                    !(List(pattern, 2) && Literal(pattern[0], "Pattern") && List(pattern[1], 1) && Literal(pattern[1][0], "S"))) return null;
                var name = column[0].Text.Replace("\"\"", "\"");
                if (!names.Add(name)) return null;
                orderedNames.Add(name);
                columns.Add(new Dictionary<string, object> { ["name"] = name });
            }
            var rows = new List<object>();
            for (var r = 0; r < rowCount; r++)
            {
                var source = table[2 + columnCount + r];
                if (!List(source, columnCount)) return null;
                var row = new Dictionary<string, object>();
                for (var c = 0; c < columnCount; c++)
                {
                    if (!Typed(source[c], "S")) return null;
                    row.Add(orderedNames[c], Text(source[c]));
                }
                rows.Add(row);
            }
            var result = new Dictionary<string, object> { ["Column"] = columnCount == 1 ? columns[0] : columns };
            if (rowCount != 0) result["Row"] = rowCount == 1 ? rows[0] : rows;
            return result;
        }

        private static object Fields(BracketsNode body, string[] names, string types)
        {
            if (!List(body, names.Length)) return null;
            var result = new Dictionary<string, object>();
            for (var i = 0; i < names.Length; i++)
            {
                var node = body[i];
                if (types[i] == 'U')
                {
                    if (!List(node, 1) || !Literal(node[0], "U")) return null;
                    result[names[i]] = null;
                }
                else if (types[i] == 'A')
                {
                    if (!List(node, 2) || !Literal(node[0], "A") || node[1].IsValueNode || node[1].Count == 0 ||
                        !Count(node[1][0], out var count) || (long)count + 1 != node[1].Count) return null;
                    var items = new List<object>();
                    for (var j = 1; j <= count; j++)
                    {
                        if (!Typed(node[1][j], "S")) return null;
                        items.Add(Text(node[1][j]));
                    }
                    result[names[i]] = count == 0 ? null : new Dictionary<string, object> { ["Item"] = count == 1 ? items[0] : items };
                }
                else
                {
                    if (!Typed(node, types[i].ToString())) return null;
                    if (types[i] == 'B')
                    {
                        if (!Literal(node[1], "0") && !Literal(node[1], "1")) return null;
                        result[names[i]] = Literal(node[1], "1") ? "true" : "false";
                    }
                    else if (types[i] == 'D')
                    {
                        if (!DateTime.TryParseExact(node[1].Text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date)) return null;
                        // Payload dates are local 1C values; only the event timestamp is UTC.
                        result[names[i]] = date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    }
                    else result[names[i]] = Text(node);
                }
            }
            return result;
        }

        private static bool Count(BracketsNode node, out int count)
        {
            count = 0;
            return node.IsValueNode && int.TryParse(node.Text, NumberStyles.None, CultureInfo.InvariantCulture, out count);
        }

        internal static string Json(object value)
        {
            var json = new StringBuilder();
            WriteJson(json, value);
            return json.ToString();
        }

        private static bool List(BracketsNode node, int count) => !node.IsValueNode && node.Count == count;
        private static bool Literal(BracketsNode node, string value) => node.IsValueNode && node.Text == value;
        private static bool Typed(BracketsNode node, string type) =>
            List(node, 2) && Literal(node[0], type) && node[1].IsValueNode;
        private static string Text(BracketsNode node) => node[1].Text.Replace("\"\"", "\"");

        internal static object TokenTree(BracketsNode node)
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
            if (value == null) json.Append("null");
            else if (value is string text)
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
