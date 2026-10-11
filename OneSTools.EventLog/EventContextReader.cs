using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // A separate dictionary prevents context capture from changing legacy lazy name resolution.
    internal sealed class EventContextReader : IDisposable
    {
        private readonly string _path;
        private readonly bool _strict;
        private readonly bool _live;
        private readonly Action<EventLogDiagnostic> _diagnostic;
        private readonly Action<EventLogProblemRecord> _problemRecordHandler;
        private readonly Dictionary<int, BracketsNode> _definitions = new Dictionary<int, BracketsNode>();
        private readonly Dictionary<(int, int), BracketsNode> _values = new Dictionary<(int, int), BracketsNode>();
        private Utf8BracketsReader _reader;
        private FileStream _stream;

        public EventContextReader(string path, bool strict, bool live, Action<EventLogDiagnostic> diagnostic,
            Action<EventLogProblemRecord> problemRecordHandler = null)
        { _path = path; _strict = strict; _live = live; _diagnostic = EventLogDiagnostic.Guard(diagnostic); _problemRecordHandler = problemRecordHandler; }

        public void Refresh(CancellationToken cancellationToken = default)
        {
            if (_reader == null)
            {
                _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _reader = new Utf8BracketsReader(_stream);
            }
            var length = _stream.Length;
            if (length < _reader.Position) throw new EventLogParseException("1Cv8.lgf", _reader.Position, "truncated dictionary");
            cancellationToken.ThrowIfCancellationRequested();
            if (length == _reader.Position) return;
            while (true)
            {
                try
                {
                    var node = _reader.NextNode(cancellationToken);
                    if (node == null) break;
                    var start = _reader.LastNodeStartPosition;
                    if (node.Count == 0 || !Number(node[0], out var type))
                        throw new EventLogParseException("1Cv8.lgf", start, "dictionary type");
                    if (_strict && type >= 1 && type <= 8)
                    {
                        var count = type == 1 || type == 5 ? 4 : 3;
                        if (node.Count != count || !Number(node[count - 1], out _) ||
                            System.Linq.Enumerable.Any(node, n => !n.IsValueNode))
                            throw new EventLogParseException("1Cv8.lgf", start, "dictionary entry");
                    }
                    if (type == 9 && node.Count == 4 && node[1].IsValueNode && node[2].IsValueNode && Number(node[3], out var id))
                        _definitions[id] = node;
                    else if (type == 10 && node.Count == 4 && Number(node[2], out var definition) && Number(node[3], out var value))
                        _values[(definition, value)] = node;
                    else if (type >= 9)
                        _diagnostic?.Invoke(new EventLogDiagnostic("DictionaryRecord", "1Cv8.lgf", start, nodesJson: StructuredEventData.Json(StructuredEventData.TokenTree(node))));
                }
                catch (Exception error)
                {
                    _reader.Position = _reader.LastNodeStartPosition;
                    if (!EventLogDiagnostic.IsHandlerFailure(error) && error is EventLogParseException && _reader.TrySkipProblemRecord("1Cv8.lgf",
                        "dictionary context", error, _problemRecordHandler, cancellationToken)) continue;
                    throw;
                }
            }
            if (_strict && !_live && _reader.HasIncompleteRecord)
            {
                var error = new EventLogParseException("1Cv8.lgf", _reader.Position, "incomplete dictionary entry");
                _reader.TrySkipProblemRecord("1Cv8.lgf", "incomplete dictionary entry", error,
                    _problemRecordHandler, cancellationToken);
                throw error;
            }
        }

        public string Wrap(string eventCode, string applicationCode, string data, BracketsNode record, string fileName, long position)
        {
            var extra = new List<object>();
            for (var i = 17; i < record.Count; i++) extra.Add(StructuredEventData.TokenTree(record[i]));
            var dictionary = new List<object>();
            var separation = record.Count > 18 ? Separation(record[18], dictionary) : null;
            if (record.Count > 18 && separation == null && !(record[18].Count == 1 && record[18][0].IsValueNode && record[18][0].Text == "0"))
                _diagnostic?.Invoke(new EventLogDiagnostic("UnresolvedSeparation", fileName, position, eventCode,
                    StructuredEventData.Json(StructuredEventData.TokenTree(record[18]))));
            return StructuredEventData.Json(new Dictionary<string, object>
            {
                ["$format"] = "1c-event-context-v1", ["EventCode"] = eventCode, ["ApplicationCode"] = applicationCode,
                ["Data"] = data, ["DataNodes"] = StructuredEventData.TokenTree(record[11]),
                ["ExtraFields"] = extra, ["SessionDataSeparation"] = separation, ["SeparationDictionary"] = dictionary
            });
        }

        private object Separation(BracketsNode node, List<object> evidence)
        {
            if (node.IsValueNode || node.Count == 0 || !Number(node[0], out var count) || (long)count * 2 + 1 != node.Count || count == 0) return null;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var resolved = true;
            for (var i = 0; i < count; i++)
            {
                if (!Number(node[1 + i * 2], out var definitionId) || !Number(node[2 + i * 2], out var valueId)) return null;
                _definitions.TryGetValue(definitionId, out var definition);
                _values.TryGetValue((definitionId, valueId), out var value);
                if (definition != null) evidence.Add(StructuredEventData.TokenTree(definition));
                if (value != null) evidence.Add(StructuredEventData.TokenTree(value));
                var decoded = value == null ? null : Scalar(value[1]);
                if (definition == null || value == null || decoded == null) { resolved = false; continue; }
                var name = definition[2].Text.Replace("\"\"", "\"");
                if (result.ContainsKey(name)) { resolved = false; continue; }
                result.Add(name, decoded);
            }
            return resolved ? result : null;
        }

        private static string Scalar(BracketsNode node)
        {
            if (node.IsValueNode || node.Count != 2 || !node[0].IsValueNode || !node[1].IsValueNode) return null;
            switch (node[0].Text)
            {
                case "S": return node[1].Text.Replace("\"\"", "\"");
                case "N": return node[1].Text;
                case "B": return node[1].Text == "1" ? "true" : node[1].Text == "0" ? "false" : null;
                case "D":
                    return DateTime.TryParseExact(node[1].Text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null;
                default: return null;
            }
        }

        private static bool Number(BracketsNode node, out int value)
        {
            value = 0;
            return node.IsValueNode && int.TryParse(node.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        public void Dispose() => _reader?.Dispose();
    }
}
