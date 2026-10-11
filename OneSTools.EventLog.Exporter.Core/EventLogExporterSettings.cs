using System;
using NodaTime;

namespace OneSTools.EventLog.Exporter.Core
{
    public class EventLogExporterSettings
    {
        public string LogFolder { get; set; } = "";
        public int Portion { get; set; } = 10000;
        public DateTimeZone TimeZone { get; set; } = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        public int WritingMaxDop { get; set; } = 1;
        public int CollectedFactor { get; set; } = 2;
        public int ReadingTimeout { get; set; } = 1;
        public bool LoadArchive { get; set; } = false;
        /// <summary>Optional local acknowledged checkpoint. Empty retains recovery from storage.</summary>
        public string CheckpointFile { get; set; } = "";
        /// <summary>Seconds allowed to drain a pending write after cancellation (1..3600).</summary>
        public int ShutdownTimeout { get; set; } = 30;
        public DateTime SkipEventsBeforeDate { get; set; }
        /// <summary>Corrected scalar/text/status values are enabled by default; final transaction outcomes are independent.</summary>
        public bool UseCorrectedValueParsing { get; set; } = true;
        /// <summary>JSON for complex/unknown Data is enabled by default; independent of scalar corrections.</summary>
        public bool UseStructuredDataParsing { get; set; } = true;
        /// <summary>Use the extended event presentation catalog by default. False retains legacy names.</summary>
        public bool UseCurrentEventNames { get; set; } = true;
        /// <summary>Wrap Data in a lossless context envelope, including separation values. Opt in.</summary>
        public bool UseEventContext { get; set; } = false;
        /// <summary>Reject invalid dates, unresolved references and incomplete archived records. Opt in.</summary>
        public bool UseStrictParsing { get; set; } = false;
        /// <summary>Optional synchronous diagnostics. NodesJson may contain personal information.</summary>
        public Action<EventLogDiagnostic> DiagnosticHandler { get; set; }
        /// <summary>Optional synchronous problem record sink in addition to the exporter logger.</summary>
        public Action<EventLogProblemRecord> ProblemRecordHandler { get; set; }
        /// <summary>Use the latest completed LGF entries, including renamed and cleared values. Enabled by default.</summary>
        public bool UseLatestDictionaryValues { get; set; } = true;
        /// <summary>Resolve transaction outcomes from available Commit/Rollback events. Enabled by default; never wait for future events.</summary>
        public bool UseFinalTransactionStatuses { get; set; } = true;

    }
}
