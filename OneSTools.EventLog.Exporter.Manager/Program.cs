using System;
using Microsoft.Extensions.Configuration;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSystemd()
                .ConfigureLogging((hostingContext, logging) =>
                {
                    var logPath = Path.Combine(hostingContext.HostingEnvironment.ContentRootPath, "log.txt");
                    logging.AddFile(logPath);
                    logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                })
                .ConfigureServices((context, services) =>
                {
                    services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(
                        context.Configuration.GetValue("Exporter:ShutdownTimeout", 30) + 5));
                    services.AddHostedService<ExportersManager>();
                });
        }
    }
}