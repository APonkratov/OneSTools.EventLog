using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NodaTime;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    internal class LgpReader : IDisposable
    {
        private readonly DateTimeZone _timeZone;
        private Utf8BracketsReader _bracketsReader;
        private bool _disposedValue;
        private FileStream _fileStream;
        private LgfReader _lgfReader;
        private FileSystemWatcher _lgpFileWatcher;
        private DateTime _skipEventsBeforeDate;
        private readonly Action<EventLogDiagnostic> _diagnostic;
        private readonly EventContextReader _context;
        private readonly Action<EventLogProblemRecord> _problemRecordHandler;
        private readonly bool _useStrictParsing;

        public LgpReader(string lgpPath, DateTimeZone timeZone, LgfReader lgfReader, DateTime skipEventsBeforeDate,
            Action<EventLogDiagnostic> diagnostic = null, EventContextReader context = null,
            Action<EventLogProblemRecord> problemRecordHandler = null, bool useStrictParsing = false)
        {
            LgpPath = lgpPath;
            _timeZone = timeZone;
            _lgfReader = lgfReader;
            _skipEventsBeforeDate = skipEventsBeforeDate;
            _diagnostic = EventLogDiagnostic.Guard(diagnostic);
            _context = context;
            _problemRecordHandler = problemRecordHandler;
            _useStrictParsing = useStrictParsing;
        }

        public string LgpPath { get; }
        public string LgpFileName => Path.GetFileName(LgpPath);
        internal string TransactionMarker { get; private set; }
        internal string TransactionKey { get; private set; }
        internal bool HasIncompleteRecord => _bracketsReader?.HasIncompleteRecord == true;
        internal long IncompletePosition => _bracketsReader?.Position ?? 0;
        internal void ReportIncompleteRecord(Exception error, CancellationToken cancellationToken) =>
            _bracketsReader.TrySkipProblemRecord(LgpFileName, "incomplete LGP record", error,
                _problemRecordHandler, cancellationToken, allowSkip: !_useStrictParsing);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public EventLogItem ReadNextEventLogItem(CancellationToken cancellationToken = default)
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(LgpReader));

            InitializeStreams();

            return ReadEventLogItemData(cancellationToken);
        }

        public void SetPosition(long position)
        {
            InitializeStreams();

            _bracketsReader.Position = position;
        }

        private void InitializeStreams()
        {
            if (_fileStream is null)
            {
                if (!File.Exists(LgpPath))
                    throw new Exception($"Cannot find lgp file by path {LgpPath}");

                _lgpFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(LgpPath)!, "*.lgp")
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName |
                                   NotifyFilters.Attributes
                };
                _lgpFileWatcher.Deleted += LgpFileWatcher_Deleted;
                _lgpFileWatcher.EnableRaisingEvents = true;

                _fileStream = new FileStream(LgpPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _bracketsReader = new Utf8BracketsReader(_fileStream);
            }
        }

        private void LgpFileWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Deleted && LgpPath == e.FullPath) Dispose();
        }

        private (StringBuilder Data, long EndPosition) ReadNextEventLogItemData(CancellationToken cancellationToken)
        {
            var data = _bracketsReader.NextNodeAsStringBuilder(cancellationToken);

            return (data, _bracketsReader.Position);
        }

        private EventLogItem ReadEventLogItemData(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StringBuilder data;
                long endPosition;
                long recordStart = -1;
                EventLogItem eventLogItem;
                try
                {
                    (data, endPosition) = ReadNextEventLogItemData(cancellationToken);
                    if (data.Length == 0)
                        return null;
                    recordStart = _bracketsReader.LastNodeStartPosition;

                    eventLogItem = ParseEventLogItemData(data, endPosition, cancellationToken);
                }
                catch (Exception error)
                {
                    if (recordStart >= 0) _bracketsReader.Position = recordStart;
                    if (EventLogDiagnostic.IsHandlerFailure(error)) throw;
                    if (!(error is ArgumentException || error is FormatException || error is OverflowException || error is InvalidDataException || error is IndexOutOfRangeException)) throw;
                    var start = _bracketsReader.LastNodeStartPosition;
                    _bracketsReader.Position = start;
                    _diagnostic?.Invoke(new EventLogDiagnostic("InvalidRecord", LgpFileName, start));
                    if (_bracketsReader.TrySkipProblemRecord(LgpFileName, "LGP record", error,
                        _problemRecordHandler, cancellationToken, allowSkip: !_useStrictParsing)) continue;
                    throw new EventLogParseException(LgpFileName, start, "LGP record", error);
                }
                if (eventLogItem != null)
                    return eventLogItem;
            }
        }

        private EventLogItem ParseEventLogItemData(StringBuilder eventLogItemData, long endPosition,
            CancellationToken cancellationToken = default)
        {
            var parsedData = JournalNodeParser.Parse(eventLogItemData);
            if (parsedData.Count < 17 || parsedData[2].IsValueNode || parsedData[2].Count != 2)
                throw new InvalidDataException("Invalid LGP record shape.");

            DateTime dateTime = default;
            try
            {
                dateTime = _timeZone.ToUtc(DateTime.ParseExact(parsedData[0], "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture));
            }
            catch
            {
                _diagnostic?.Invoke(new EventLogDiagnostic("InvalidDate", LgpFileName, _bracketsReader.LastNodeStartPosition));
                throw;
            }

            if (dateTime < _skipEventsBeforeDate)
                return null;

            var eventLogItem = new EventLogItem
            {
                DateTime = dateTime,
                TransactionStatus = GetCorrectedTransactionPresentation(parsedData[1]),
                FileName = LgpFileName,
                EndPosition = endPosition
            };

            var transactionData = parsedData[2];
            TransactionMarker = parsedData[1];
            TransactionKey = TransactionOutcomeIndex.Key(parsedData);
            eventLogItem.TransactionNumber = Convert.ToInt64(transactionData[1], 16);

            var transactionDate = new DateTime().AddSeconds(Convert.ToInt64(transactionData[0], 16) / 10000);
            eventLogItem.TransactionDateTime = transactionDate == DateTime.MinValue
                ? transactionDate
                : _timeZone.ToUtc(transactionDate);

            var (value, uuid) = _lgfReader.GetReferencedObjectValue(ObjectType.Users, parsedData[3], cancellationToken);
            eventLogItem.UserUuid = uuid;
            eventLogItem.User = DecodeText(value);

            eventLogItem.Computer = DecodeText(_lgfReader.GetObjectValue(ObjectType.Computers, parsedData[4], cancellationToken));

            var application = _lgfReader.GetObjectValue(ObjectType.Applications, parsedData[5], cancellationToken);
            eventLogItem.Application = GetApplicationPresentation(DecodeText(application));

            eventLogItem.Connection = parsedData[6];

            var ev = _lgfReader.GetObjectValue(ObjectType.Events, parsedData[7], cancellationToken);
            var eventCode = DecodeText(ev);
            eventLogItem.Event = CurrentEventNames.Get(eventCode);
            if (eventCode != null && eventLogItem.Event == eventCode && eventCode.StartsWith("_$", StringComparison.Ordinal))
                _diagnostic?.Invoke(new EventLogDiagnostic("UnknownEvent", LgpFileName, _bracketsReader.LastNodeStartPosition, eventCode));

            var severity = (string)parsedData[8];
            eventLogItem.Severity = GetSeverityPresentation(severity);

            eventLogItem.Comment = DecodeText(parsedData[9]);

            (value, uuid) = _lgfReader.GetReferencedObjectValue(ObjectType.Metadata, parsedData[10], cancellationToken);
            eventLogItem.MetadataUuid = uuid;
            eventLogItem.Metadata = DecodeText(value);

            var data = parsedData[11];
            if (StructuredEventData.ShouldParse(data))
                eventLogItem.Data = StructuredEventData.Parse(ev, data);
            else
                eventLogItem.Data = GetCorrectedData(data);
            if (eventLogItem.Data.StartsWith("{\"$format\":\"1c-brackets-v1\"", StringComparison.Ordinal))
                _diagnostic?.Invoke(new EventLogDiagnostic("UnknownDataFormat", LgpFileName, _bracketsReader.LastNodeStartPosition, ev, eventLogItem.Data));
            if (parsedData.Count > 19 || (parsedData.Count > 17 && parsedData[17].Text != "0"))
                _diagnostic?.Invoke(new EventLogDiagnostic("ExtraLgpFields", LgpFileName, _bracketsReader.LastNodeStartPosition, ev));
            if (_context != null)
                eventLogItem.Data = _context.Wrap(DecodeText(ev), DecodeText(application), eventLogItem.Data, parsedData, LgpFileName, _bracketsReader.LastNodeStartPosition);
            eventLogItem.DataPresentation = DecodeText(parsedData[12]);
            eventLogItem.Server = DecodeText(_lgfReader.GetObjectValue(ObjectType.Servers, parsedData[13], cancellationToken));

            var mainPort = _lgfReader.GetObjectValue(ObjectType.MainPorts, parsedData[14], cancellationToken);
            if (mainPort != "")
                eventLogItem.MainPort = int.Parse(mainPort);

            var addPort = _lgfReader.GetObjectValue(ObjectType.AddPorts, parsedData[15], cancellationToken);
            if (addPort != "")
                eventLogItem.AddPort = int.Parse(addPort);

            eventLogItem.Session = parsedData[16];
            if (eventLogItem.User == null || eventLogItem.Computer == null ||
                application == null || ev == null || eventLogItem.Metadata == null ||
                eventLogItem.Server == null || mainPort == null || addPort == null)
                throw new InvalidDataException("Unresolved LGF reference.");

            // The checkpoint must include all dictionary entries needed by this event.
            eventLogItem.LgfEndPosition = _lgfReader.GetPosition();

            return eventLogItem;
        }

        private static string DecodeText(string value) => value?.Replace("\"\"", "\"");

        private static string GetCorrectedData(BracketsNode node)
        {
            switch ((string)node[0])
            {
                case "N":
                    // Keep the invariant decimal token: double/decimal can lose valid 1C precision.
                    return node[1];
                case "D":
                    // A 1C Date value has no time zone; only the event timestamp is converted to UTC.
                    return DateTime.ParseExact(node[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                        .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                case "S":
                    return ((string)node[1]).Replace("\"\"", "\"");
                case "R": return node[1];
                case "U": return "";
                case "B": return (string)node[1] == "0" ? "false" : "true";
                default: throw new InvalidDataException("Unsupported scalar data format.");
            }
        }

        private static string GetCorrectedTransactionPresentation(string str)
        {
            return str switch
            {
                "U" => "Не завершена",
                "C" => "Зафиксирована",
                "R" => "Отменена",
                "N" => "Нет транзакции",
                _ => ""
            };
        }

        private static string GetSeverityPresentation(string str)
        {
            return str switch
            {
                "I" => "Информация",
                "E" => "Ошибка",
                "W" => "Предупреждение",
                "N" => "Примечание",
                _ => ""
            };
        }

        private static string GetApplicationPresentation(string str)
        {
            return str switch
            {
                "1CV8" => "Толстый клиент",
                "1CV8C" => "Тонкий клиент",
                "WebClient" => "Веб-клиент",
                "Designer" => "Конфигуратор",
                "COMConnection" => "Внешнее соединение (COM, обычное)",
                "WSConnection" => "Сессия web-сервиса",
                "BackgroundJob" => "Фоновое задание",
                "SystemBackgroundJob" => "Системное фоновое задание",
                "SrvrConsole" => "Консоль кластера",
                "COMConsole" => "Внешнее соединение (COM, административное)",
                "JobScheduler" => "Планировщик заданий",
                "Debugger" => "Отладчик",
                "RAS" => "Сервер администрирования",
                _ => str
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            _bracketsReader?.Dispose();
            _bracketsReader = null;
            _fileStream = null;

            _lgpFileWatcher?.Dispose();
            _lgpFileWatcher = null;

            _lgfReader = null;

            _disposedValue = true;
        }

        ~LgpReader()
        {
            Dispose(false);
        }
    }
}
