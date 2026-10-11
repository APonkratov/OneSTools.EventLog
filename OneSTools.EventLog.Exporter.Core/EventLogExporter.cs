using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace OneSTools.EventLog.Exporter.Core
{
    public class EventLogExporter : IDisposable
    {
        private readonly int _collectedFactor;
        private readonly bool _loadArchive;

        // Exporter settings
        private readonly string _logFolder;
        private readonly ILogger<EventLogExporter> _logger;
        private readonly int _portion;
        private readonly int _readingTimeout;
        private readonly IEventLogStorage _storage;
        private readonly DateTimeZone _timeZone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        private readonly int _writingMaxDop;
        private readonly string _checkpointFile;
        private readonly int _shutdownTimeout;
        private int _started;
        private readonly DateTime _skipEventsBeforeDate;
        private readonly bool _useCorrectedValueParsing;
        private readonly bool _useLatestDictionaryValues;
        private readonly bool _useFinalTransactionStatuses;
        private readonly bool _useStructuredDataParsing;
        private readonly bool _useStrictParsing;
        private readonly bool _useEventContext;
        private readonly bool _useCurrentEventNames;

        private readonly Action<EventLogDiagnostic> _diagnosticHandler;
        private readonly Action<EventLogProblemRecord> _problemRecordHandler;
        private EventLogPosition _lastSkippedPosition;
        private EventLogPosition _acknowledgedPosition;
        private readonly HashSet<string> _seenDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        private string _currentLgpFile;

        private bool _disposedValue;

        private EventLogReader _eventLogReader;

        public EventLogExporter(EventLogExporterSettings settings, IEventLogStorage storage,
            ILogger<EventLogExporter> logger = null)
        {
            _logger = logger;
            _storage = storage;

            _logFolder = settings.LogFolder;
            _portion = settings.Portion;
            _writingMaxDop = settings.WritingMaxDop;
            _collectedFactor = settings.CollectedFactor;
            _loadArchive = settings.LoadArchive;
            _checkpointFile = settings.CheckpointFile;
            _shutdownTimeout = settings.ShutdownTimeout;
            _timeZone = settings.TimeZone;
            _readingTimeout = settings.ReadingTimeout;
            _skipEventsBeforeDate = settings.SkipEventsBeforeDate;
            _useCorrectedValueParsing = settings.UseCorrectedValueParsing;
            _useLatestDictionaryValues = settings.UseLatestDictionaryValues;
            _useFinalTransactionStatuses = settings.UseFinalTransactionStatuses;
            _diagnosticHandler = settings.DiagnosticHandler;
            _problemRecordHandler = settings.ProblemRecordHandler;
            _useStructuredDataParsing = settings.UseStructuredDataParsing;
            _useStrictParsing = settings.UseStrictParsing;
            _useEventContext = settings.UseEventContext;
            _useCurrentEventNames = settings.UseCurrentEventNames;

            CheckSettings();
        }

        public EventLogExporter(ILogger<EventLogExporter> logger, IConfiguration configuration,
            IEventLogStorage storage)
        {
            _logger = logger;
            _storage = storage;

            _logFolder = configuration.GetValue("Exporter:LogFolder", "");
            _portion = configuration.GetValue("Exporter:Portion", 10000);
            _writingMaxDop = configuration.GetValue("Exporter:WritingMaxDegreeOfParallelism", 1);
            _collectedFactor = configuration.GetValue("Exporter:CollectedFactor", 2);
            _loadArchive = configuration.GetValue("Exporter:LoadArchive", false);
            _checkpointFile = configuration.GetValue("Exporter:CheckpointFile", "");
            _shutdownTimeout = configuration.GetValue("Exporter:ShutdownTimeout", 30);
            _readingTimeout = configuration.GetValue("Exporter:ReadingTimeout", 1);
            _skipEventsBeforeDate = configuration.GetValue("Exporter:SkipEventsBeforeDate", DateTime.MinValue);
            _useCorrectedValueParsing = configuration.GetValue("Exporter:UseCorrectedValueParsing", false);
            _useLatestDictionaryValues = configuration.GetValue("Exporter:UseLatestDictionaryValues", false);
            _useFinalTransactionStatuses = configuration.GetValue("Exporter:UseFinalTransactionStatuses", false);
            _useStructuredDataParsing = configuration.GetValue("Exporter:UseStructuredDataParsing", false);
            _useStrictParsing = configuration.GetValue("Exporter:UseStrictParsing", false);
            _useEventContext = configuration.GetValue("Exporter:UseEventContext", false);
            _useCurrentEventNames = configuration.GetValue("Exporter:UseCurrentEventNames", false);

            var timeZone = configuration.GetValue("Exporter:TimeZone", "");

            if (!string.IsNullOrWhiteSpace(timeZone))
            {
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone);

                _timeZone = zone ?? throw new Exception($"\"{timeZone}\" is unknown time zone");
            }

            CheckSettings();
        }

        private void CheckSettings()
        {
            if (_logFolder == string.Empty)
                throw new Exception("Event log folder is not specified");

            if (!Directory.Exists(_logFolder))
                throw new Exception($"Event log folder ({_logFolder}) doesn't exist");

            if (_portion <= 0) throw new ArgumentOutOfRangeException("Portion");
            if (_shutdownTimeout <= 0 || _shutdownTimeout > 3600) throw new ArgumentOutOfRangeException("ShutdownTimeout");
            if (_readingTimeout < -1 || _readingTimeout > int.MaxValue / 1000) throw new ArgumentOutOfRangeException("ReadingTimeout");
            if (_writingMaxDop <= 0)
                throw new Exception("WritingMaxDegreeOfParallelism cannot be equal to or less than 0");

            if (_collectedFactor <= 0)
                throw new Exception("CollectedFactor cannot be equal to or less than 0");
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposedValue) throw new ObjectDisposedException(nameof(EventLogExporter));
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("An exporter instance can only be started once.");
            if (cancellationToken.IsCancellationRequested) return;

            _logger?.LogInformation($"Log folder: {_logFolder}; portion per request: {_portion}");
            if (_loadArchive) _logger?.LogInformation("Archive mode: export will complete at EOF.");
            _logger?.LogInformation(string.IsNullOrEmpty(_checkpointFile)
                ? "Local checkpoint is disabled; recovery uses the storage cursor."
                : "Local acknowledged checkpoint is enabled.");
            if (_writingMaxDop != 1)
                _logger?.LogWarning("Writes are serialized per source to preserve contiguous delivery; WritingMaxDegreeOfParallelism is retained for configuration compatibility.");

            using var checkpoint = string.IsNullOrEmpty(_checkpointFile) ? null : new DeliveryCheckpoint(_checkpointFile,
                _logFolder, (_storage as IEventLogStorageIdentity)?.CheckpointIdentity ?? _storage.GetType().FullName);
            using var writing = new CancellationTokenSource();
            using var stop = cancellationToken.Register(() => writing.CancelAfter(TimeSpan.FromSeconds(_shutdownTimeout)));
            var shutdown = Task.Delay(Timeout.Infinite, writing.Token);
            var batch = new List<EventLogItem>(_portion);
            try
            {
                EventLogReaderSettings settings;
                try { settings = await GetReaderSettingsAsync(checkpoint, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                _eventLogReader = new EventLogReader(settings);
                while (!cancellationToken.IsCancellationRequested)
                {
                    EventLogItem item;
                    try { item = _eventLogReader.ReadNextEventLogItem(cancellationToken); }
                    catch (EventLogReaderTimeoutException)
                    {
                        await WriteBatchAsync(batch, checkpoint, shutdown, writing.Token);
                        continue;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch
                    {
                        // A valid prefix can be acknowledged before surfacing a later parse failure.
                        await WriteBatchAsync(batch, checkpoint, shutdown, writing.Token);
                        throw;
                    }
                    if (item == null)
                    {
                        if (!settings.LiveMode) break;
                        continue;
                    }
                    batch.Add(item);
                    if (_currentLgpFile != item.FileName)
                    {
                        _logger?.LogInformation($"Reader started reading {item.FileName}");
                        _currentLgpFile = item.FileName;
                    }
                    if (batch.Count == _portion)
                        await WriteBatchAsync(batch, checkpoint, shutdown, writing.Token);
                }
                await WriteBatchAsync(batch, checkpoint, shutdown, writing.Token);
            }
            finally
            {
                _eventLogReader?.Dispose();
                _eventLogReader = null;
                writing.Cancel();
            }
        }

        private async Task WriteBatchAsync(List<EventLogItem> batch, DeliveryCheckpoint checkpoint,
            Task shutdown, CancellationToken writingToken)
        {
            if (batch.Count == 0)
            {
                SaveProgressAfterProblems(checkpoint, _acknowledgedPosition);
                return;
            }
            // No read-ahead or later INSERT is allowed until this batch is acknowledged.
            var write = _storage.WriteEventLogDataAsync(batch, writingToken);
            if (await Task.WhenAny(write, shutdown) != write)
            {
                // Observe late failure of a non-cooperative custom storage without advancing its checkpoint.
                _ = write.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new TimeoutException("Exporter shutdown deadline expired; the unconfirmed batch must be replayed.");
            }
            try { await write; }
            catch (OperationCanceledException) when (writingToken.IsCancellationRequested)
            {
                throw new TimeoutException("Exporter shutdown deadline expired; the unconfirmed batch must be replayed.");
            }
            var last = batch[batch.Count - 1];
            SaveProgressAfterProblems(checkpoint, new EventLogPosition(last.FileName, last.EndPosition, last.LgfEndPosition, last.Id));
            batch.Clear();
        }

        private void SaveProgressAfterProblems(DeliveryCheckpoint checkpoint, EventLogPosition position)
        {
            // A skipped tail has no storage row. Persist it only after the entire
            // preceding batch has been acknowledged, including an empty batch at EOF.
            var skipped = _lastSkippedPosition;
            if (skipped != null && (position == null ||
                StringComparer.Ordinal.Compare(skipped.FileName, position.FileName) > 0 ||
                (skipped.FileName == position.FileName && skipped.EndPosition > position.EndPosition)))
                position = new EventLogPosition(skipped.FileName, skipped.EndPosition,
                    position?.LgfEndPosition ?? 0, position?.Id ?? 0);
            if (position == null || (position.FileName == _acknowledgedPosition?.FileName &&
                position.EndPosition == _acknowledgedPosition.EndPosition && position.Id == _acknowledgedPosition.Id &&
                position.LgfEndPosition == _acknowledgedPosition.LgfEndPosition)) return;
            checkpoint?.Save(position);
            _acknowledgedPosition = position;
        }

        private void ReportProblemRecord(EventLogProblemRecord problem)
        {
            _problemRecordHandler?.Invoke(problem);
            var canLog = _logger?.IsEnabled(LogLevel.Error) == true;
            if (canLog)
                _logger.LogError(problem.Error,
                    "Problem journal record in {LogFolder}/{FileName} at bytes [{Position}, {EndPosition}); stage {Stage}; {ErrorType}: {Reason}; skipped {Skipped}; encoding {Encoding}; raw {RawRecordJson}",
                    _logFolder, problem.FileName, problem.Position, problem.EndPosition, problem.Stage,
                    problem.Error.GetBaseException().GetType().Name, problem.Error.GetBaseException().Message,
                    problem.Skipped, problem.Encoding, problem.RawRecordJson);
            else if (_problemRecordHandler == null)
                throw new IOException("Problem record logging is disabled; refusing to discard journal data.");
            // Auxiliary dictionary/context/transaction scans can run ahead of delivery.
            if (problem.Skipped && problem.Stage == "LGP record")
                _lastSkippedPosition = new EventLogPosition(problem.FileName, problem.EndPosition, 0, 0);
        }

        private void ReportParsingDiagnostic(EventLogDiagnostic diagnostic)
        {
            _diagnosticHandler?.Invoke(diagnostic);
            // Keep normal exporter logs free of payloads and repeated per-event warnings.
            if (_seenDiagnostics.Count < 1000 && _seenDiagnostics.Add(diagnostic.Code + "|" + diagnostic.EventCode))
                _logger?.LogWarning("Journal diagnostic {Code} in {FileName} at byte {Position}; event {EventCode}.",
                    diagnostic.Code, diagnostic.FileName, diagnostic.Position, diagnostic.EventCode);
        }

        private async Task<EventLogReaderSettings> GetReaderSettingsAsync(DeliveryCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            var eventLogReaderSettings = new EventLogReaderSettings
            {
                LogFolder = _logFolder,
                LiveMode = true,
                ReadingTimeout = _readingTimeout == -1 ? Timeout.Infinite : _readingTimeout * 1000,
                TimeZone = _timeZone,
                SkipEventsBeforeDate = _skipEventsBeforeDate,
                UseCorrectedValueParsing = _useCorrectedValueParsing,
                UseLatestDictionaryValues = _useLatestDictionaryValues,
                UseFinalTransactionStatuses = _useFinalTransactionStatuses,
                UseStructuredDataParsing = _useStructuredDataParsing,
                UseCurrentEventNames = _useCurrentEventNames,
                UseEventContext = _useEventContext,
                UseStrictParsing = _useStrictParsing,
                DiagnosticHandler = ReportParsingDiagnostic,
                ProblemRecordHandler = _logger != null || _problemRecordHandler != null
                    ? (Action<EventLogProblemRecord>)ReportProblemRecord : null
            };

            EventLogPosition position;
            var localPosition = checkpoint != null && checkpoint.Exists;
            if (localPosition) position = checkpoint.Read();
            else
            {
                position = _loadArchive ? null : await _storage.ReadEventLogPositionAsync(cancellationToken);
            }
            if (position != null)
            {
                if (string.IsNullOrEmpty(position.FileName) || position.FileName.IndexOfAny(new[] {'/', '\\'}) >= 0)
                    throw new InvalidDataException("Invalid saved journal file name.");
                var path = Path.Combine(_logFolder, position.FileName);
                if (!File.Exists(path))
                    throw new FileNotFoundException("The acknowledged journal file is missing; restore the journal or explicitly start a new archive import.", path);
                var lgpLength = new FileInfo(path).Length;
                if (position.EndPosition < 0 || position.EndPosition > lgpLength + (localPosition ? 0 : 1) || position.LgfEndPosition < 0 || position.Id < 0)
                    throw new InvalidDataException("Saved journal position is outside the available journal.");
                var dictionary = Path.Combine(_logFolder, "1Cv8.lgf");
                if (!File.Exists(dictionary))
                    throw new InvalidDataException("The acknowledged LGF dictionary is missing.");
                var lgfLength = new FileInfo(dictionary).Length;
                if (position.LgfEndPosition > lgfLength + (localPosition ? 0 : 1))
                    throw new InvalidDataException("The acknowledged LGF position is outside the available dictionary.");
                // Historical StreamReader cursors could be one byte past EOF. Accept them only when
                // bootstrapping from storage, and persist exact offsets in the new local format.
                position = new EventLogPosition(position.FileName, Math.Min(position.EndPosition, lgpLength),
                    Math.Min(position.LgfEndPosition, lgfLength), position.Id);
                eventLogReaderSettings.LgpFileName = position.FileName;
                eventLogReaderSettings.LgpStartPosition = position.EndPosition;
                eventLogReaderSettings.LgfStartPosition = position.LgfEndPosition;
                eventLogReaderSettings.ItemId = position.Id;
            }
            // Persist even an empty starting position before the first INSERT can partially succeed.
            if (!localPosition) checkpoint?.Save(position);
            _acknowledgedPosition = position;
            _logger?.LogInformation(position == null ? "Reading from the beginning of the available journal." :
                $"Resuming {position.FileName} at {position.EndPosition}; LGF at {position.LgfEndPosition}; Id {position.Id}.");
            eventLogReaderSettings.LiveMode = !_loadArchive;
            return eventLogReaderSettings;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposedValue)
                return;

            if (disposing) _storage?.Dispose();

            _eventLogReader?.Dispose();

            _disposedValue = true;
        }

        ~EventLogExporter()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
