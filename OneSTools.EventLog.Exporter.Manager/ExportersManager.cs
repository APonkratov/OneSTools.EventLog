using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Core.ClickHouse;
using OneSTools.EventLog.Exporter.Core.ElasticSearch;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class ExportersManager : BackgroundService
    {
        private readonly List<ClstFolder> _clstFolders;
        private readonly List<ClstWatcher> _clstWatchers = new();

        private readonly int _collectedFactor;

        // ClickHouse
        private readonly string _connectionString;
        private readonly bool _loadArchive;
        private readonly ILogger<ExportersManager> _logger;
        private readonly int _maximumRetries;

        private readonly TimeSpan _maxRetryTimeout;

        // ElasticSearch
        private readonly List<ElasticSearchNode> _nodes;
        private readonly int _portion;
        private readonly int _readingTimeout;
        private readonly Dictionary<string, ExportRun> _runExporters = new();
        private bool _stopping;
        private readonly string _checkpointDirectory;
        private readonly int _shutdownTimeout;

        private sealed class ExportRun
        {
            public readonly CancellationTokenSource Cancellation = new();
            public Task Task;
            public bool StopRequested;
            public bool Completed;
        }
        private readonly string _separation;

        private readonly IServiceProvider _serviceProvider;

        // Common settings
        private readonly StorageType _storageType;
        private readonly DateTimeZone _timeZone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        private readonly int _writingMaxDop;
        private readonly DateTime _skipEventsBeforeDate;
        private readonly bool _useCorrectedValueParsing;
        private readonly bool _useLatestDictionaryValues;
        private readonly bool _useFinalTransactionStatuses;
        private readonly bool _useStructuredDataParsing;
        private readonly bool _useStrictParsing;
        private readonly bool _useEventContext;
        private readonly bool _useCurrentEventNames;

        public ExportersManager(ILogger<ExportersManager> logger, IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            _clstFolders = configuration.GetSection("Manager:ClstFolders").Get<List<ClstFolder>>();
            _storageType = configuration.GetValue("Exporter:StorageType", StorageType.None);
            _portion = configuration.GetValue("Exporter:Portion", 10000);
            _writingMaxDop = configuration.GetValue("Exporter:WritingMaxDegreeOfParallelism", 1);
            _collectedFactor = configuration.GetValue("Exporter:CollectedFactor", 2);
            _loadArchive = configuration.GetValue("Exporter:LoadArchive", false);
            _checkpointDirectory = configuration.GetValue("Exporter:CheckpointDirectory", "");
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
                _timeZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ??
                            throw new Exception($"\"{timeZone}\" is unknown time zone");

            CheckSettings();

            switch (_storageType)
            {
                case StorageType.ClickHouse:
                    {
                        _connectionString = configuration.GetValue("ClickHouse:ConnectionString", "");
                        if (_connectionString == string.Empty)
                            throw new Exception("Connection string is not specified");
                        break;
                    }
                case StorageType.ElasticSearch:
                    {
                        _nodes = configuration.GetSection("ElasticSearch:Nodes").Get<List<ElasticSearchNode>>();
                        if (_nodes == null)
                            throw new Exception("ElasticSearch nodes are not specified");

                        _separation = configuration.GetValue("ElasticSearch:Separation", "H");
                        _maximumRetries = configuration.GetValue("ElasticSearch:MaximumRetries",
                            ElasticSearchStorage.DefaultMaximumRetries);
                        _maxRetryTimeout = TimeSpan.FromSeconds(configuration.GetValue("ElasticSearch:MaxRetryTimeout",
                            ElasticSearchStorage.DefaultMaxRetryTimeoutSec));
                        break;
                    }
            }
        }

        private void CheckSettings()
        {
            if (_clstFolders == null || _clstFolders.Count == 0)
                throw new Exception("\"ClstFolders\" is not specified");

            foreach (var clstFolder in _clstFolders.Where(clstFolder => !Directory.Exists(clstFolder.Folder)))
                throw new Exception($"Clst folder ({clstFolder.Folder}) doesn't exist");

            if (_portion <= 0) throw new ArgumentOutOfRangeException("Portion");
            if (_readingTimeout < -1 || _readingTimeout > int.MaxValue / 1000) throw new ArgumentOutOfRangeException("ReadingTimeout");
            if (_shutdownTimeout <= 0 || _shutdownTimeout > 3600) throw new ArgumentOutOfRangeException("ShutdownTimeout");
            if (_writingMaxDop <= 0)
                throw new Exception("WritingMaxDegreeOfParallelism cannot be equal to or less than 0");

            if (_collectedFactor <= 0)
                throw new Exception("CollectedFactor cannot be equal to or less than 0");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                foreach (var clstFolder in _clstFolders)
                {
                    var watcher = new ClstWatcher(clstFolder.Folder, clstFolder.Templates);
                    _clstWatchers.Add(watcher);
                    foreach (var (key, (name, dataBaseName)) in watcher.InfoBases)
                        StartExporter(key, name, dataBaseName);
                    watcher.InfoBasesAdded += ClstWatcher_InfoBasesAdded;
                    watcher.InfoBasesDeleted += ClstWatcher_InfoBasesDeleted;
                }
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally
            {
                Task[] tasks;
                lock (_runExporters)
                {
                    _stopping = true;
                    foreach (var run in _runExporters.Values)
                    {
                        run.StopRequested = true;
                        if (!run.Completed) run.Cancellation.Cancel();
                    }
                    tasks = _runExporters.Values.Select(run => run.Task).ToArray();
                }
                foreach (var watcher in _clstWatchers)
                {
                    watcher.InfoBasesAdded -= ClstWatcher_InfoBasesAdded;
                    watcher.InfoBasesDeleted -= ClstWatcher_InfoBasesDeleted;
                    watcher.Dispose();
                }
                // Await the actual async workers, including any predecessor still draining after deletion/re-addition.
                await Task.WhenAll(tasks);
            }
        }

        private void ClstWatcher_InfoBasesDeleted(object sender, ClstEventArgs args)
        {
            StopExporter(args.Path, args.Name);
        }

        private void ClstWatcher_InfoBasesAdded(object sender, ClstEventArgs args)
        {
            StartExporter(args.Path, args.Name, args.DataBaseName);
        }

        private void StartExporter(string path, string name, string dataBaseName)
        {
            var logFolder = Path.Combine(path, "1Cv8Log");

            // Check this is an old event log format
            var lgfPath = Path.Combine(logFolder, "1Cv8.lgf");

            var needStart = File.Exists(lgfPath);

            if (needStart)
            {
                lock (_runExporters)
                {
                    if (!_stopping && (!_runExporters.TryGetValue(path, out var current) || current.StopRequested))
                    {
                        var previous = _runExporters.TryGetValue(path, out var old) ? old.Task : Task.CompletedTask;
                        var run = new ExportRun();
                        var token = run.Cancellation.Token;
                        var logger =
                            (ILogger<EventLogExporter>)_serviceProvider.GetService(typeof(ILogger<EventLogExporter>));

                        var settings = new EventLogExporterSettings
                        {
                            LogFolder = logFolder,
                            CollectedFactor = _collectedFactor,
                            LoadArchive = _loadArchive,
                            ShutdownTimeout = _shutdownTimeout,
                            CheckpointFile = GetCheckpointFile(logFolder, dataBaseName),
                            Portion = _portion,
                            ReadingTimeout = _readingTimeout,
                            TimeZone = _timeZone,
                            WritingMaxDop = _writingMaxDop,
                            SkipEventsBeforeDate = _skipEventsBeforeDate,
                            UseCorrectedValueParsing = _useCorrectedValueParsing,
                            UseLatestDictionaryValues = _useLatestDictionaryValues,
                            UseFinalTransactionStatuses = _useFinalTransactionStatuses,
                            UseStructuredDataParsing = _useStructuredDataParsing,
                            UseCurrentEventNames = _useCurrentEventNames,
                            UseEventContext = _useEventContext,
                            UseStrictParsing = _useStrictParsing
                        };

                        run.Task = Task.Run(async () =>
                        {
                            try
                            {
                                // A re-added IB must not overlap a previous worker's final INSERT.
                                await previous;
                                while (!token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        using var storage = GetStorage(dataBaseName);
                                        using var exporter = new EventLogExporter(settings, storage, logger);
                                        await exporter.StartAsync(token);
                                        if (_loadArchive) break;
                                    }
                                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                                    catch (Exception ex)
                                    {
                                        _logger?.LogCritical(ex,
                                            "Failed to execute EventLogExporter for {LogFolder} to {Database}; {ErrorType}: {Reason}. Retry delay: {RetryDelayMs} ms.",
                                            logFolder, dataBaseName, ex.GetBaseException().GetType().Name,
                                            ex.GetBaseException().Message, 5000);
                                    }
                                    if (token.IsCancellationRequested) break;
                                    await Task.Delay(5000, token);
                                }
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                            finally
                            {
                                lock (_runExporters)
                                {
                                    run.Completed = true;
                                    run.Cancellation.Dispose();
                                }
                            }
                        });
                        _runExporters[path] = run;

                        _logger?.LogInformation(
                            $"Event log exporter for \"{name}\" information base to \"{dataBaseName}\" is started");
                    }
                }
            }
            else
            {
                _logger?.LogInformation(
                    $"Event log of \"{name}\" information base is in \"new\" format, it won't be handled");
            }
        }

        private void StopExporter(string id, string name)
        {
            lock (_runExporters)
            {
                if (_runExporters.TryGetValue(id, out var run))
                {
                    run.StopRequested = true;
                    if (!run.Completed) run.Cancellation.Cancel();
                    _logger?.LogInformation($"Event log exporter for \"{name}\" information base stop requested");
                }
            }
        }

        private string GetCheckpointFile(string logFolder, string database)
        {
            if (string.IsNullOrEmpty(_checkpointDirectory)) return "";
            using var sha = SHA256.Create();
            var source = Path.GetFullPath(logFolder);
            if (Path.DirectorySeparatorChar == '\\') source = source.ToUpperInvariant();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source + "\n" + database));
            var name = string.Concat(hash.Select(value => value.ToString("x2")));
            return Path.Combine(_checkpointDirectory, name + ".json");
        }

        protected virtual IEventLogStorage GetStorage(string dataBaseName)
        {
            switch (_storageType)
            {
                case StorageType.ClickHouse:
                    {
                        var logger =
                            (ILogger<ClickHouseStorage>)_serviceProvider.GetService(typeof(ILogger<ClickHouseStorage>));
                        var connectionString = $"{_connectionString}Database={dataBaseName};";

                        return new ClickHouseStorage(connectionString, logger);
                    }
                case StorageType.ElasticSearch:
                    {
                        var logger =
                            (ILogger<ElasticSearchStorage>)_serviceProvider.GetService(
                                typeof(ILogger<ElasticSearchStorage>));

                        var settings = new ElasticSearchStorageSettings
                        {
                            Index = dataBaseName,
                            Separation = _separation,
                            MaximumRetries = _maximumRetries,
                            MaxRetryTimeout = _maxRetryTimeout
                        };
                        settings.Nodes.AddRange(_nodes);

                        return new ElasticSearchStorage(settings, logger);
                    }
                case StorageType.None:
                    throw new Exception("StorageType parameter is not specified");
                default:
                    throw new Exception("Try to get a storage for unknown StorageType value");
            }
        }

    }
}
