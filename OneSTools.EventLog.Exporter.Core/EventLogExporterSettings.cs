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
        public DateTime SkipEventsBeforeDate { get; set; }
        /// <summary>Enable corrected scalar/text/status values; false preserves historical output.</summary>
        public bool UseCorrectedValueParsing { get; set; } = false;
        /// <summary>Enable JSON for complex/unknown Data; independent of scalar corrections.</summary>
        public bool UseStructuredDataParsing { get; set; } = false;
        /// <summary>Use the latest completed LGF entries, including renamed and cleared values. Opt in.</summary>
        public bool UseLatestDictionaryValues { get; set; } = false;
        /// <summary>Resolve transaction outcomes from available Commit/Rollback events. Opt in.</summary>
        public bool UseFinalTransactionStatuses { get; set; } = false;

    }
}
