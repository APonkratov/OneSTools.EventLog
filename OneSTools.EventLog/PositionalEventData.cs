using System;
using System.Collections.Generic;
using System.Globalization;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // Field positions checked against controlled writes and an administrator's
    // UnloadEventLog export from 8.3.27.2325; the offline oracle returned null.
    // The full wire tree is retained alongside the named values.
    internal static class PositionalEventData
    {
        private static readonly string[] UserNames =
        {
            "OSAuthentication", "StandardAuthentication", "CannotChangePassword", "Name", "Language",
            "PasswordChanged", "PasswordIsSet", "ShowInList", "FullName", "OSUser", "RunMode", "Roles", "DefaultInterface",
            "OpenIDAuthentication", "UnsafeOperationProtection", "SecondAuthenticationFactorSettings",
            "SecondAuthenticationFactorSettingsProcessing", "CannotRecoveryPassword", "Email", "OpenIDConnectAuthentication",
            "AccessTokenAuthentication", "UserMatchingKeys", "PasswordPolicyName", "PasswordSettingDate", "PasswordHashAlgorithmType",
            "QRCodeAuthentication", "PasswordDoesNotSatisfyRequirements", "EmailAuthentication"
        };

        private static readonly string[] PolicyNames =
        {
            "Name", "PasswordMaxEffectivePeriod", "PasswordMinEffectivePeriod", "PasswordMinLength",
            "PasswordReuseLimit", "PasswordStrengthCheck", "PasswordExpirationNotificationPeriod",
            "PasswordCompromiseCheck", "ActionOnPasswordRequirementsViolationOnAuthenticationCode"
        };

        public static object Parse(string code, BracketsNode node)
        {
            var body = node[1];
            switch (code)
            {
                case "_$User$_.New":
                case "_$User$_.Update":
                case "_$User$_.NewError":
                case "_$User$_.UpdateError":
                    if (body.Count == 29 && Literal(body[0], "65535"))
                        return Layout(node, UserNames, "BBBSOBBBSSSAOBBTSBSBBTSDSBBB");
                    if (body.Count == 28 && Literal(body[0], "65534"))
                    {
                        // The 65534 layout omits position 13 of the 65535 layout.
                        var names = new List<string>(UserNames);
                        names.RemoveAt(12);
                        return Layout(node, names.ToArray(), "BBBSOBBBSSSABBTSBSBBTSDSBBB");
                    }
                    break;
                case "_$InfoBase$_.UserPasswordPolicyNew":
                case "_$InfoBase$_.UserPasswordPolicyNewError":
                case "_$InfoBase$_.UserPasswordPolicyUpdate":
                    if (body.Count == 10 && Literal(body[0], "255"))
                        return Layout(node, PolicyNames, "SNNNNBNBN");
                    break;
                case "_$InfoBase$_.ParametersUpdate":
                case "_$InfoBase$_.ParametersUpdateError":
                    if (body.Count == 4 && Literal(body[0], "448"))
                        return Layout(node, new[] { "MaxUnsuccessfulAttemptsCount", "LockDuration", "UserNameAdditionCodes" }, "NNS");
                    if (body.Count == 3 && Literal(body[0], "192"))
                        return Layout(node, new[] { "MaxUnsuccessfulAttemptsCount", "LockDuration" }, "NN");
                    if (body.Count == 3 && Literal(body[0], "320"))
                        return Layout(node, new[] { "MaxUnsuccessfulAttemptsCount", "UserNameAdditionCodes" }, "NS");
                    if (body.Count == 3 && Literal(body[0], "384"))
                        return Layout(node, new[] { "LockDuration", "UserNameAdditionCodes" }, "NS");
                    if (body.Count == 2)
                    {
                        // Only observed masks are accepted; ordering for other combinations
                        // is not inferred from the bit values.
                        string name;
                        switch (body[0].Text)
                        {
                            case "1": name = "LockWaitTime"; break;
                            case "2": name = "UserPasswordMinLength"; break;
                            case "4":
                                if (!TypedNumber(body[1], "0") && !TypedNumber(body[1], "3")) return null;
                                return Parameter(node, "UserPasswordStrengthCheck", TypedNumber(body[1], "3") ? "true" : "false");
                            case "8": name = "PassiveSessionHibernateTime"; break;
                            case "16": name = "HibernateSessionTerminateTime"; break;
                            case "64": name = "MaxUnsuccessfulAttemptsCount"; break;
                            case "128": name = "LockDuration"; break;
                            case "256":
                                return StringParameter(node, "UserNameAdditionCodes");
                            case "512": name = "UserPasswordMinEffectivePeriod"; break;
                            case "1024": name = "UserPasswordMaxEffectivePeriod"; break;
                            case "2048": name = "UserPasswordReuseLimit"; break;
                            case "4096": name = "UserPasswordExpirationNotificationPeriod"; break;
                            case "8192":
                                return StringParameter(node, "UserPasswordHashAlgorithmType");
                            case "16384":
                                return Layout(node, new[] { "UserPasswordCompromiseCheck" }, "B");
                            case "32768":
                                if (TypedNumber(body[1], "0")) return Parameter(node,
                                    "ActionOnUserPasswordRequirementsViolationOnAuthentication", "Потребовать смену пароля");
                                if (TypedNumber(body[1], "1")) return Parameter(node,
                                    "ActionOnUserPasswordRequirementsViolationOnAuthentication", "Предложить смену пароля");
                                if (TypedNumber(body[1], "2")) return Parameter(node,
                                    "ActionOnUserPasswordRequirementsViolationOnAuthentication", "Нет");
                                return null;
                            case "65536": name = "InactivityTimeForTerminateSessionNotification"; break;
                            case "131072": name = "InactivityTimeForTerminateSession"; break;
                            default: return null;
                        }
                        return Layout(node, new[] { name }, "N");
                    }
                    break;
            }
            return null;
        }

        private static bool TypedNumber(BracketsNode node, string value) => !node.IsValueNode && node.Count == 2 &&
            Literal(node[0], "N") && Literal(node[1], value);

        private static object StringParameter(BracketsNode node, string name)
        {
            var value = Value(node[1][1], 'S');
            return value == null ? null : Parameter(node, name, value);
        }

        private static object Parameter(BracketsNode node, string name, object value) => new Dictionary<string, object>
        {
            ["$format"] = "1c-positional-data-v1", ["layout"] = "P/" + node[1][0].Text,
            ["values"] = new Dictionary<string, object> { [name] = value },
            ["unnamedFields"] = new List<object>(), ["nodes"] = StructuredEventData.TokenTree(node)
        };

        private static object Layout(BracketsNode node, string[] names, string types)
        {
            var body = node[1];
            var values = new Dictionary<string, object>();
            var unnamed = new List<object>();
            for (var i = 0; i < names.Length; i++)
            {
                var value = Value(body[i + 1], types[i]);
                if (value == null) return null;
                if (names[i] == null) unnamed.Add((i + 1).ToString(CultureInfo.InvariantCulture));
                else values.Add(names[i], value);
            }
            return new Dictionary<string, object>
            {
                ["$format"] = "1c-positional-data-v1", ["layout"] = "P/" + body[0].Text,
                ["values"] = values, ["unnamedFields"] = unnamed, ["nodes"] = StructuredEventData.TokenTree(node)
            };
        }

        private static object Value(BracketsNode node, char type)
        {
            if (node.IsValueNode || node.Count != 2 || !Literal(node[0], type == 'T' ? "A" : type.ToString())) return null;
            var payload = node[1];
            if (type == 'O')
            {
                if (payload.IsValueNode || payload.Count != 3 || !Literal(payload[0], "#") ||
                    !payload[1].IsValueNode || !payload[2].IsValueNode ||
                    !Guid.TryParseExact(payload[1].Text, "D", out _) || !Guid.TryParseExact(payload[2].Text, "D", out _)) return null;
                return new Dictionary<string, object> { ["$type"] = "O", ["typeId"] = payload[1].Text, ["value"] = payload[2].Text };
            }
            if (type == 'A' || type == 'T')
            {
                if (payload.IsValueNode || payload.Count == 0 || !payload[0].IsValueNode ||
                    !int.TryParse(payload[0].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    (long)count + 1 != payload.Count) return null;
                var items = new List<object>();
                for (var i = 1; i <= count; i++)
                {
                    var value = Value(payload[i], type == 'T' ? 'S' : 'O');
                    if (value == null) return null;
                    items.Add(value);
                }
                return items;
            }
            if (!payload.IsValueNode) return null;
            switch (type)
            {
                case 'S': return payload.Text.Replace("\"\"", "\"");
                case 'B': return Literal(payload, "1") ? "true" : Literal(payload, "0") ? "false" : null;
                case 'N': return payload.Text;
                case 'D': return DateTime.TryParseExact(payload.Text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null;
                default: return null;
            }
        }

        private static bool Literal(BracketsNode node, string text) => node.IsValueNode && node.Text == text;
    }
}
