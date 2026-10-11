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
        /// <summary>Enable corrected scalar/text/status values; false preserves historical output.</summary>
        public bool UseCorrectedValueParsing { get; set; } = false;
        /// <summary>Enable JSON for complex/unknown Data; independent of scalar corrections.</summary>
        public bool UseStructuredDataParsing { get; set; } = false;
        /// <summary>Use the extended event presentation catalog. False retains legacy names.</summary>
        public bool UseCurrentEventNames { get; set; } = false;
        /// <summary>Wrap Data in a lossless context envelope, including separation values. Opt in.</summary>
        public bool UseEventContext { get; set; } = false;
        /// <summary>Reject invalid dates, unresolved references and incomplete archived records. Opt in.</summary>
        public bool UseStrictParsing { get; set; } = false;
        /// <summary>Optional synchronous diagnostics. NodesJson may contain personal information.</summary>
        public Action<EventLogDiagnostic> DiagnosticHandler { get; set; }
        /// <summary>Optional synchronous problem record sink in addition to the exporter logger.</summary>
        public Action<EventLogProblemRecord> ProblemRecordHandler { get; set; }
        /// <summary>Use the latest completed LGF entries, including renamed and cleared values. Opt in.</summary>
        public bool UseLatestDictionaryValues { get; set; } = false;
        /// <summary>Resolve transaction outcomes from available Commit/Rollback events. Opt in.</summary>
        public bool UseFinalTransactionStatuses { get; set; } = false;

    }
}
