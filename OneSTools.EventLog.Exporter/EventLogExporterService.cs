using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter
{
    public class EventLogExporterService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<EventLogExporterService> _logger;
        private readonly bool _loadArchive;
        private readonly IHostApplicationLifetime _lifetime;

        public EventLogExporterService(IServiceProvider serviceProvider, ILogger<EventLogExporterService> logger)
            : this(serviceProvider, logger, null, null) { }

        public EventLogExporterService(IServiceProvider serviceProvider, ILogger<EventLogExporterService> logger,
            IConfiguration configuration, IHostApplicationLifetime lifetime)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _loadArchive = configuration?.GetValue("Exporter:LoadArchive", false) ?? false;
            _lifetime = lifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var exporter = scope.ServiceProvider.GetRequiredService<EventLogExporter>();
                    await exporter.StartAsync(stoppingToken);
                    if (_loadArchive)
                    {
                        _lifetime?.StopApplication();
                        return;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogCritical(ex,
                        "Failed to execute EventLogExporter; {ErrorType}: {Reason}. Retry delay: {RetryDelayMs} ms.",
                        ex.GetBaseException().GetType().Name, ex.GetBaseException().Message, 5000);
                }
                if (stoppingToken.IsCancellationRequested) break;
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }

    }
}
