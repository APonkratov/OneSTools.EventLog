using System;
using System.IO;
using System.Text;
using System.Threading;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // Journal delimiters are ASCII, so framing can count the original UTF-8 bytes
    // without depending on StreamReader's decoder or its private buffer fields.
    internal sealed class Utf8BracketsReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _length;
        private long _position;
        public long LastNodeStartPosition { get; private set; }
        public bool HasIncompleteRecord { get; private set; }

        public Utf8BracketsReader(Stream stream)
        {
            _stream = stream;
            _position = stream.Position;
        }

        public long Position
        {
            get => _position;
            set
            {
                _stream.Seek(value, SeekOrigin.Begin);
                _position = value;
                _offset = _length = 0;
            }
        }

        private int ReadByte(CancellationToken cancellationToken)
        {
            if (_offset == _length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _length = _stream.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_length == 0) return -1;
            }
            _position++;
            return _buffer[_offset++];
        }

        public StringBuilder NextNodeAsStringBuilder(CancellationToken cancellationToken = default)
        {
            long start = -1;
            HasIncompleteRecord = false;
            var depth = 0;
            var quoted = false;
            using var bytes = new MemoryStream();
            try
            {
                int value;
                while ((value = ReadByte(cancellationToken)) != -1)
                {
                    if (start < 0)
                    {
                        if (value != '{') continue; // BOM, header, separators.
                        start = Position - 1;
                        LastNodeStartPosition = start;
                    }
                    bytes.WriteByte((byte)value);
                    if (value == '"') quoted = !quoted; // Doubled quotes toggle twice.
                    if (quoted) continue;
                    if (value == '{' && ++depth > 256) throw new InvalidDataException("Journal nesting exceeds 256 levels.");
                    else if (value == '}' && --depth == 0)
                        return new StringBuilder(new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length));
                }
            }
            catch
            {
                if (start >= 0) Position = start;
                throw;
            }
            // Keep an incomplete record, including a split UTF-8 code point, for retry.
            if (start >= 0) { Position = start; HasIncompleteRecord = true; }
            return new StringBuilder();
        }

        public BracketsNode NextNode(CancellationToken cancellationToken = default)
        {
            try
            {
                var text = NextNodeAsStringBuilder(cancellationToken);
                return text.Length == 0 ? null : JournalNodeParser.Parse(text);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is FormatException)
            {
                Position = LastNodeStartPosition;
                throw new EventLogParseException(Path.GetFileName((_stream as FileStream)?.Name ?? "journal"), LastNodeStartPosition, "brackets", error);
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
