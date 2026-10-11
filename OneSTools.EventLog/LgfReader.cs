using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    internal class LgfReader : IDisposable
    {
        private Utf8BracketsReader _bracketsReader;
        private bool _disposedValue;
        private FileStream _fileStream;
        private readonly Action<EventLogDiagnostic> _diagnostic;
        private readonly Action<EventLogProblemRecord> _problemRecordHandler;
        private readonly bool _live;

        /// <summary>
        ///     Key tuple - first value is an object type, second value is an object number in the array
        /// </summary>
        private Dictionary<(ObjectType, int), string> _objects = new Dictionary<(ObjectType, int), string>();

        /// <summary>
        ///     Key tuple - first value is an object type, second value is an object number in the array
        ///     Value tuple - first value is an object value, second value is a guid of the object value
        /// </summary>
        private Dictionary<(ObjectType, int), (string, string)> _referencedObjects =
            new Dictionary<(ObjectType, int), (string, string)>();

        public LgfReader(string lgfPath, Action<EventLogDiagnostic> diagnostic = null,
            Action<EventLogProblemRecord> problemRecordHandler = null, bool live = false)
        {
            LgfPath = lgfPath;
            _diagnostic = EventLogDiagnostic.Guard(diagnostic);
            _problemRecordHandler = problemRecordHandler;
            _live = live;
        }

        public string LgfPath { get; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void ReadTill(ObjectType objectType, int number, long position,
            CancellationToken cancellationToken = default)
        {
            InitializeStreams();

            var stop = false;

            while (!stop && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var itemData = _bracketsReader.NextNode(cancellationToken);
                    if (itemData == null) break;
                    var ot = (ObjectType) (int) itemData[0];

                    if (ot >= ObjectType.Users && ot < ObjectType.Unknown)
                    {
                        var count = ot == ObjectType.Users || ot == ObjectType.Metadata ? 4 : 3;
                        if (itemData.Count != count || itemData.Any(node => !node.IsValueNode))
                            throw new InvalidDataException("Invalid LGF record shape.");
                    }

                    // Skip unknown object types
                    if (ot >= ObjectType.Unknown)
                    {
                        _diagnostic?.Invoke(new EventLogDiagnostic("DictionaryRecord", "1Cv8.lgf", _bracketsReader.LastNodeStartPosition,
                            nodesJson: StructuredEventData.Json(StructuredEventData.TokenTree(itemData))));
                        continue;
                    }

                    switch (ot)
                    {
                        case ObjectType.Users:
                        case ObjectType.Metadata:
                            var key = (ot, (int) itemData[3]);
                            var value = ((string) itemData[2], (string) itemData[1]);

                            if (_referencedObjects.ContainsKey(key))
                                _referencedObjects.Remove(key);

                            _referencedObjects.Add(key, value);

                            if (objectType == ObjectType.None && GetPosition() >= position - 1)
                            {
                                stop = true;
                                break;
                            }

                            if (ot == objectType && key.Item2 == number)
                                stop = true;

                            break;
                        default:
                            var key1 = (ot, (int) itemData[2]);
                            var value1 = (string) itemData[1];

                            if (_objects.ContainsKey(key1))
                                _objects.Remove(key1);

                            _objects.Add(key1, value1);

                            if (objectType == ObjectType.None && GetPosition() >= position - 1)
                            {
                                stop = true;
                                break;
                            }

                            if (ot == objectType && key1.Item2 == number)
                                stop = true;

                            break;
                    }
                }
                catch (Exception error)
                {
                    var start = _bracketsReader.LastNodeStartPosition;
                    _bracketsReader.Position = start;
                    if (EventLogDiagnostic.IsHandlerFailure(error)) throw;
                    if (!(error is ArgumentException || error is FormatException || error is OverflowException ||
                        error is InvalidDataException || error is IndexOutOfRangeException || error is EventLogParseException)) throw;
                    _diagnostic?.Invoke(new EventLogDiagnostic("InvalidDictionary", "1Cv8.lgf", start));
                    if (_bracketsReader.TrySkipProblemRecord("1Cv8.lgf", "dictionary entry", error,
                        _problemRecordHandler, cancellationToken)) continue;
                    throw new EventLogParseException("1Cv8.lgf", start, "dictionary entry", error);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_live && _bracketsReader.HasIncompleteRecord)
            {
                var error = new EventLogParseException("1Cv8.lgf", _bracketsReader.Position, "incomplete dictionary entry");
                _bracketsReader.TrySkipProblemRecord("1Cv8.lgf", "incomplete dictionary entry", error,
                    _problemRecordHandler, cancellationToken);
                throw error;
            }
        }

        public string GetObjectValue(ObjectType objectType, int number, CancellationToken cancellationToken = default)
        {
            if (number == 0)
                return "";

            if (_objects.TryGetValue((objectType, number), out var value))
                return value;
            ReadTill(objectType, number, 0, cancellationToken);

            if (_objects.TryGetValue((objectType, number), out value))
                return value;
            
            return null;
        }

        public (string Value, string Uuid) GetReferencedObjectValue(ObjectType objectType, int number,
            CancellationToken cancellationToken = default)
        {
            if (number == 0)
                return ("", "");

            if (_referencedObjects.TryGetValue((objectType, number), out var value))
                return value;
            ReadTill(objectType, number, 0, cancellationToken);

            if (_referencedObjects.TryGetValue((objectType, number), out value))
                return value;

            return (null, null);
        }

        private void InitializeStreams()
        {
            if (_fileStream is null)
            {
                if (!File.Exists(LgfPath))
                    throw new Exception("Cannot find \"1Cv8.lgf\"");

                _fileStream = new FileStream(LgfPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _bracketsReader = new Utf8BracketsReader(_fileStream);
            }
        }

        public void SetPosition(long position, CancellationToken cancellationToken = default)
        {
            ReadTill(ObjectType.None, 0, position, cancellationToken);
        }

        // Read completed appended entries, including updates of already cached keys.
        // An incomplete trailing entry is left at its opening brace for the next call.
        public void Refresh(CancellationToken cancellationToken = default)
        {
            InitializeStreams();
            var length = _fileStream.Length;
            if (length < _bracketsReader.Position)
                throw new IOException("LGF was truncated; restart the reader to rebuild its dictionary.");
            cancellationToken.ThrowIfCancellationRequested();
            // Position is the consumed byte cursor, not the buffered stream position.
            // An incomplete entry rewinds it, so its tail is still retried.
            if (length == _bracketsReader.Position) return;
            ReadTill(ObjectType.None, 0, long.MaxValue, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public long GetPosition()
        {
            InitializeStreams();

            return _bracketsReader.Position;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _objects = null;
                    _referencedObjects = null;
                }

                _bracketsReader?.Dispose();
                _bracketsReader = null;
                _fileStream?.Dispose();
                _fileStream = null;

                _disposedValue = true;
            }
        }

        ~LgfReader()
        {
            Dispose(false);
        }
    }
}
