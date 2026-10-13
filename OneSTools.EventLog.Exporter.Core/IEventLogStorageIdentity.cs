namespace OneSTools.EventLog.Exporter.Core
{
    /// <summary>Stable destination identity for binding an optional local checkpoint. Must not contain credentials.</summary>
    public interface IEventLogStorageIdentity
    {
        string CheckpointIdentity { get; }
    }
}
