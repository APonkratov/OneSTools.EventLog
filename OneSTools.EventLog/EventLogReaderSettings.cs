using System;
using System.Threading;
using NodaTime;

namespace OneSTools.EventLog
{
    public class EventLogReaderSettings
    {
        public string LogFolder { get; set; } = "";
        public bool LiveMode { get; set; } = true;
        public string LgpFileName { get; set; } = "";
        public long LgpStartPosition { get; set; } = 0;
        public long LgfStartPosition { get; set; } = 0;
        public long ItemId { get; set; } = 0;
        public int ReadingTimeout { get; set; } = Timeout.Infinite;
        public DateTimeZone TimeZone { get; set; } = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        public DateTime SkipEventsBeforeDate { get; set; }
        /// <summary>Wrap Data in a lossless context envelope, including separation values. Opt in.</summary>
        /// <summary>Diagnostic mode: log malformed records and fail without skipping. Does not reject unknown valid formats.</summary>
        public bool UseStrictParsing { get; set; } = false;

        public bool UseEventContext { get; set; } = false;
        /// <summary>Optional synchronous diagnostics. NodesJson may contain personal information.</summary>
        public Action<EventLogDiagnostic> DiagnosticHandler { get; set; }
        /// <summary>Opt in to logging and skipping complete malformed records. A throwing handler keeps the cursor for retry.</summary>
        public Action<EventLogProblemRecord> ProblemRecordHandler { get; set; }

    }
}
