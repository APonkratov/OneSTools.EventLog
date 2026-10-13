using System;

namespace OneSTools.EventLog
{
    /// <summary>Original content of a problematic journal record or tail. May contain personal data.</summary>
    public sealed class EventLogProblemRecord
    {
        public string FileName { get; }
        public long Position { get; }
        public long EndPosition { get; }
        public string Stage { get; }
        public Exception Error { get; }
        /// <summary>UTF-8 text, or Base64 when the original bytes are not valid UTF-8.</summary>
        public string RawRecord { get; }
        public string Encoding { get; }
        /// <summary>False for a diagnostic that retains historical fallback output or cannot safely skip a tail.</summary>
        public bool Skipped { get; }
        /// <summary>JSON string literal, suitable for a single log line without losing newlines or quotes.</summary>
        public string RawRecordJson => StructuredEventData.Json(RawRecord);

        internal EventLogProblemRecord(string fileName, long position, long endPosition,
            string stage, Exception error, string rawRecord, string encoding, bool skipped = true)
        {
            FileName = fileName; Position = position; EndPosition = endPosition;
            Stage = stage; Error = error; RawRecord = rawRecord; Encoding = encoding;
            Skipped = skipped;
        }
    }
}
