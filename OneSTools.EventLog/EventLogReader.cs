using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneSTools.EventLog
{
    /// <summary>
    ///     Presents methods for reading 1C event log
    /// </summary>
    public class EventLogReader : IDisposable
    {
        private readonly EventLogReaderSettings _settings;
        private bool _disposedValue;
        private LgfReader _lgfReader;
        private ManualResetEvent _lgpChangedCreated;
        private FileSystemWatcher _lgpFilesWatcher;
        private LgpReader _lgpReader;
        private string _lastLgpFileName = string.Empty;
        private readonly TransactionOutcomeIndex _transactionOutcomes;
        private EventLogItem _pendingItem;
        private readonly EventContextReader _eventContext;

        public EventLogReader(EventLogReaderSettings settings)
        {
            _settings = settings;

            _lgfReader = new LgfReader(Path.Combine(_settings.LogFolder, "1Cv8.lgf"), settings.DiagnosticHandler, settings.ProblemRecordHandler, settings.LiveMode, settings.UseStrictParsing);
            _lgfReader.SetPosition(settings.LgfStartPosition);
            if (settings.UseEventContext)
                _eventContext = new EventContextReader(Path.Combine(settings.LogFolder, "1Cv8.lgf"),
                    settings.LiveMode, settings.DiagnosticHandler, settings.ProblemRecordHandler, settings.UseStrictParsing);
            _transactionOutcomes = new TransactionOutcomeIndex(settings.LogFolder, settings.LiveMode, settings.ProblemRecordHandler, settings.UseStrictParsing);

            if (settings.LgpFileName != string.Empty)
            {
                var file = Path.Combine(_settings.LogFolder, settings.LgpFileName);

                _lgpReader = new LgpReader(file, settings.TimeZone, _lgfReader, settings.SkipEventsBeforeDate,
                    settings.DiagnosticHandler, settings.UseEventContext ? _eventContext : null,
                    settings.ProblemRecordHandler, settings.UseStrictParsing);
                _lgpReader.SetPosition(settings.LgpStartPosition);
                _lastLgpFileName = settings.LgpFileName;
            }
        }

        /// <summary>
        ///     Current reader's "lgp" file name
        /// </summary>
        public string LgpFileName => _lgpReader.LgpFileName;

        public void Dispose()
        {
            // Не изменяйте этот код. Разместите код очистки в методе "Dispose(bool disposing)".
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     The behaviour of the method depends on the mode of the reader. In the "live" mode it'll be waiting for an appearing
        ///     of the new event item, otherwise It'll just return null
        /// </summary>
        /// <param name="cancellationToken">Token for interrupting of the reader</param>
        /// <returns></returns>
        public EventLogItem ReadNextEventLogItem(CancellationToken cancellationToken = default)
        {
            if (_lgpReader == null)
                SetNextLgpReader(cancellationToken);

            if (_settings.LiveMode && _lgpFilesWatcher == null)
                StartLgpFilesWatcher();

            EventLogItem item = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _eventContext?.Refresh(cancellationToken);
                    _lgfReader.Refresh(cancellationToken);
                    item = _pendingItem ?? _lgpReader?.ReadNextEventLogItem(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    item = null;
                    _lgpReader = null;
                    break;
                }

                if (item == null)
                {
                    var newReader = SetNextLgpReader(cancellationToken);

                    if (_settings.LiveMode)
                    {
                        if (!newReader)
                        {
                            _lgpChangedCreated.Reset();

                            var waitHandle = WaitHandle.WaitAny(
                                new[] {_lgpChangedCreated, cancellationToken.WaitHandle}, _settings.ReadingTimeout);

                            if (_settings.ReadingTimeout != Timeout.Infinite && waitHandle == WaitHandle.WaitTimeout)
                                throw new EventLogReaderTimeoutException();

                            _lgpChangedCreated.Reset();
                        }
                    }
                    else
                    {
                        if (!newReader)
                            break;
                    }
                }
                else
                {
                    _pendingItem = item;
                    item.TransactionStatus = _transactionOutcomes.Resolve(_lgpReader.TransactionMarker,
                        _lgpReader.TransactionKey, cancellationToken);
                    _pendingItem = null;
                    _settings.ItemId++;
                    item.Id = _settings.ItemId;

                    break;
                }
            }

            return item;
        }

        private bool SetNextLgpReader(CancellationToken cancellationToken)
        {
            // Platform LGP names contain the period in yyyyMMddHHmmss format.
            // Modification times may be equal or reordered after copying/restoring logs.
            // SkipEventsBeforeDate is applied to event timestamps by LgpReader, not mtime.
            var nextFile = Directory.GetFiles(_settings.LogFolder, "*.lgp")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault(file => string.IsNullOrEmpty(_lastLgpFileName) ||
                    StringComparer.Ordinal.Compare(Path.GetFileName(file), _lastLgpFileName) > 0);

            if (_lgpReader?.HasIncompleteRecord == true &&
                (!_settings.LiveMode || !string.IsNullOrEmpty(nextFile)))
            {
                _settings.DiagnosticHandler?.Invoke(new EventLogDiagnostic("IncompleteRecord", _lgpReader.LgpFileName, _lgpReader.IncompletePosition));
                var error = new EventLogParseException(_lgpReader.LgpFileName, _lgpReader.IncompletePosition, "incomplete LGP record");
                _lgpReader.ReportIncompleteRecord(error, cancellationToken);
                throw error;
            }

            if (string.IsNullOrEmpty(nextFile))
            {
                return false;
            }

            _lgpReader?.Dispose();
            _lgpReader = null;

            _lgpReader = new LgpReader(nextFile, _settings.TimeZone, _lgfReader, _settings.SkipEventsBeforeDate,
                _settings.DiagnosticHandler, _settings.UseEventContext ? _eventContext : null,
                _settings.ProblemRecordHandler, _settings.UseStrictParsing);
            _lastLgpFileName = Path.GetFileName(nextFile);

            return true;
        }

        private void StartLgpFilesWatcher()
        {
            _lgpChangedCreated = new ManualResetEvent(false);

            _lgpFilesWatcher = new FileSystemWatcher(_settings.LogFolder, "*.lgp")
            {
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _lgpFilesWatcher.Changed += LgpFilesWatcher_Event;
            _lgpFilesWatcher.Created += LgpFilesWatcher_Event;
            _lgpFilesWatcher.EnableRaisingEvents = true;
        }

        private void LgpFilesWatcher_Event(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed)
            {
                // FileSystemWatcher can have a callback in flight after Dispose returns.
                var signal = _lgpChangedCreated;
                try { signal?.Set(); }
                catch (ObjectDisposedException) { }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                _lgpFilesWatcher?.Dispose();
                _lgpFilesWatcher = null;
                _lgpChangedCreated?.Dispose();
                _lgpChangedCreated = null;
                _lgfReader?.Dispose();
                _eventContext?.Dispose();
                _transactionOutcomes?.Dispose();
                _lgfReader = null;
                _lgpReader?.Dispose();
                _lgpReader = null;

                _disposedValue = true;
            }
        }

        ~EventLogReader()
        {
            Dispose(false);
        }
    }
}
