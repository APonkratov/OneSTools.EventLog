using System;
using System.IO;

namespace OneSTools.EventLog
{
    /// <summary>Optional diagnostic evidence. NodesJson can contain personal data; do not log it indiscriminately.</summary>
    public sealed class EventLogDiagnostic
    {
        public string Code { get; }
        public string FileName { get; }
        public long Position { get; }
        public string EventCode { get; }
        public string NodesJson { get; }
        public EventLogDiagnostic(string code, string fileName, long position, string eventCode = "", string nodesJson = null)
        { Code = code; FileName = fileName; Position = position; EventCode = eventCode; NodesJson = nodesJson; }
    }

    /// <summary>A failed record is left at its original byte position for retry, never silently skipped.</summary>
    public sealed class EventLogParseException : IOException
    {
        public string FileName { get; }
        public long Position { get; }
        public string Stage { get; }
        public EventLogParseException(string fileName, long position, string stage, Exception inner = null)
            : base($"Cannot parse {stage} in {fileName} at byte {position}.", inner)
        { FileName = fileName; Position = position; Stage = stage; }
    }
}
