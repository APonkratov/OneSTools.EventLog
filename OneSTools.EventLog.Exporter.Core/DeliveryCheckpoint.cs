using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace OneSTools.EventLog.Exporter.Core
{
    // Only acknowledged contiguous delivery is stored here, never the read-ahead position.
    internal sealed class DeliveryCheckpoint : IDisposable
    {
        private readonly string _path;
        private readonly string _source;
        private readonly string _destination;
        private readonly FileStream _lock;
        private readonly DataContractJsonSerializer _serializer = new DataContractJsonSerializer(typeof(State));
        public bool Exists => File.Exists(_path);

        public DeliveryCheckpoint(string path, string source, string destination)
        {
            _path = Path.GetFullPath(path);
            _source = Path.GetFullPath(source);
            using var sha = SHA256.Create();
            _destination = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(destination)));
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _lock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (_lock.Length != 0 && !Exists)
            {
                _lock.Dispose();
                throw new InvalidDataException("An initialized checkpoint is missing; restore it instead of inferring progress from partially committed rows.");
            }
        }

        public EventLogPosition Read()
        {
            using var file = File.OpenRead(_path);
            var state = (State)_serializer.ReadObject(file);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (state == null || state.Version != 1 || !string.Equals(state.Source, _source, comparison) || state.Destination != _destination)
                throw new InvalidDataException("Checkpoint version, source or destination does not match this exporter.");
            if (!state.HasPosition) { MarkInitialized(); return null; }
            if (string.IsNullOrEmpty(state.FileName) || state.FileName.IndexOfAny(new[] {'/', '\\'}) >= 0 ||
                state.EndPosition < 0 || state.LgfEndPosition < 0 || state.Id < 0)
                throw new InvalidDataException("Invalid delivery checkpoint position.");
            MarkInitialized();
            return new EventLogPosition(state.FileName, state.EndPosition, state.LgfEndPosition, state.Id);
        }

        public void Save(EventLogPosition position)
        {
            var state = new State
            {
                Version = 1, Source = _source, Destination = _destination, HasPosition = position != null,
                FileName = position?.FileName, EndPosition = position?.EndPosition ?? 0,
                LgfEndPosition = position?.LgfEndPosition ?? 0, Id = position?.Id ?? 0
            };
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    _serializer.WriteObject(file, state);
                    file.Flush(true);
                }
                // Same-directory replacement; never delete the old checkpoint before publishing the new one.
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                MarkInitialized();
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public void Dispose() => _lock.Dispose();

        private void MarkInitialized()
        {
            if (_lock.Length != 0) return;
            _lock.WriteByte(1);
            _lock.Flush(true);
        }

        [DataContract]
        private sealed class State
        {
            [DataMember(IsRequired = true)] public int Version;
            [DataMember(IsRequired = true)] public string Source;
            [DataMember(IsRequired = true)] public string Destination;
            [DataMember(IsRequired = true)] public bool HasPosition;
            [DataMember(IsRequired = true)] public string FileName;
            [DataMember(IsRequired = true)] public long EndPosition;
            [DataMember(IsRequired = true)] public long LgfEndPosition;
            [DataMember(IsRequired = true)] public long Id;
        }
    }
}
