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
        /// <summary>
        /// Opt in to scalar N/D/S decoding, text unescaping and corrected transaction statuses.
        /// False preserves the historical output. Complex P data keeps its legacy representation.
        /// </summary>
        public bool UseCorrectedValueParsing { get; set; } = false;
    }
}
