using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // The on-disk U marker describes the event at write time. Its outcome may be
    // recorded later, in a different period and beyond the delivery checkpoint.
    internal sealed class TransactionOutcomeIndex : IDisposable
    {
        private readonly string _folder;
        private readonly bool _live;
        private readonly LgfReader _dictionary;
        private readonly Action<EventLogProblemRecord> _problemRecordHandler;
        private readonly Dictionary<string, long> _positions = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _initialized;

        public TransactionOutcomeIndex(string folder, bool live, Action<EventLogProblemRecord> problemRecordHandler = null)
        {
            _folder = folder;
            _live = live;
            _problemRecordHandler = problemRecordHandler;
            // A separate dictionary must not refresh the legacy presentation cache.
            _dictionary = new LgfReader(Path.Combine(folder, "1Cv8.lgf"), problemRecordHandler: problemRecordHandler);
        }

        internal static string Key(BracketsNode record) =>
            Convert.ToInt64(record[2][0], 16).ToString(CultureInfo.InvariantCulture) + ":" +
            Convert.ToInt64(record[2][1], 16).ToString(CultureInfo.InvariantCulture) + ":" +
            ((long)record[6]).ToString(CultureInfo.InvariantCulture) + ":" + (string)record[13];

        public string Resolve(string marker, string key, CancellationToken cancellationToken)
        {
            switch (marker)
            {
                case "N": return "Нет транзакции";
                case "C": return "Зафиксирована";
                case "R": return "Отменена";
                case "U":
                    if (!_initialized || (_live && !_outcomes.ContainsKey(key))) Refresh(cancellationToken);
                    return _outcomes.TryGetValue(key, out var outcome) ? outcome : "Не завершена";
                default: return marker;
            }
        }

        private void Refresh(CancellationToken cancellationToken)
        {
            _dictionary.Refresh(cancellationToken);
            foreach (var file in Directory.GetFiles(_folder, "*.lgp").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = _positions.TryGetValue(file, out var previous) ? previous : 0;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < position)
                    throw new IOException("LGP was truncated; restart the reader to rebuild transaction outcomes.");
                using var reader = new Utf8BracketsReader(stream);
                reader.Position = position;
                while (true)
                {
                    try
                    {
                        var record = reader.NextNode(cancellationToken);
                        if (record == null) break;
                        var code = _dictionary.GetObjectValue(ObjectType.Events, record[7], cancellationToken);
                        if (code == null)
                            throw new InvalidDataException("Cannot resolve an LGP event reference while indexing transaction outcomes.");
                        if (code == "_$Transaction$_.Commit" || code == "_$Transaction$_.Rollback")
                        {
                            var key = Key(record);
                            var status = code == "_$Transaction$_.Commit" ? "Зафиксирована" : "Отменена";
                            if (_outcomes.TryGetValue(key, out var existing) && existing != status)
                                throw new InvalidDataException("Conflicting transaction outcomes in the available journal.");
                            _outcomes[key] = status;
                        }
                        _positions[file] = reader.Position;
                    }
                    catch (Exception error) when (!EventLogDiagnostic.IsHandlerFailure(error) &&
                        (error is EventLogParseException || error is InvalidDataException || error is ArgumentException ||
                        error is FormatException || error is OverflowException || error is IndexOutOfRangeException))
                    {
                        reader.Position = reader.LastNodeStartPosition;
                        if (!reader.TrySkipProblemRecord(Path.GetFileName(file), "transaction index", error,
                            _problemRecordHandler, cancellationToken)) throw;
                        _positions[file] = reader.Position;
                    }
                }
                _positions[file] = reader.Position;
            }
            cancellationToken.ThrowIfCancellationRequested();
            _initialized = true;
        }

        public void Dispose() => _dictionary.Dispose();
    }
}
