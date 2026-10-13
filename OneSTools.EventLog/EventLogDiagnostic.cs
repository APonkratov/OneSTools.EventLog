using System;
using System.IO;

namespace OneSTools.EventLog
{
    /// <summary>Optional diagnostic evidence. NodesJson can contain personal data; do not log it indiscriminately.</summary>
    public sealed class EventLogDiagnostic
    {
        private static readonly object HandlerFailure = new object();

        // Preserve the callback's original exception type, but distinguish it from
        // a format error in the journal. User code must never cause a record skip.
        internal static Action<EventLogDiagnostic> Guard(Action<EventLogDiagnostic> handler)
        {
            if (handler == null) return null;
            return diagnostic =>
            {
                try { handler(diagnostic); }
                catch (Exception error) { error.Data[HandlerFailure] = true; throw; }
            };
        }

        internal static bool IsHandlerFailure(Exception error) => error.Data.Contains(HandlerFailure);

        public string Code { get; }
        public string FileName { get; }
        public long Position { get; }
        public string EventCode { get; }
        public string NodesJson { get; }
        public EventLogDiagnostic(string code, string fileName, long position, string eventCode = "", string nodesJson = null)
        { Code = code; FileName = fileName; Position = position; EventCode = eventCode; NodesJson = nodesJson; }
    }

    /// <summary>A record that cannot be recovered is left at its original byte position for retry.</summary>
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
