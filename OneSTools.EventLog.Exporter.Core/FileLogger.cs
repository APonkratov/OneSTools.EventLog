using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OneSTools.EventLog.Exporter.Core
{
    public class FileLogger : ILogger
    {
        private static readonly object Locker = new object();
        private readonly string _categoryName;
        private readonly string _path;
        private readonly long _maxFileBytes;
        private readonly int _retainedFiles;

        public FileLogger(string path, string categoryName)
            : this(path, categoryName, 10 * 1024 * 1024, 5) { }

        public FileLogger(string path, string categoryName, long maxFileBytes, int retainedFiles)
        {
            if (maxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            if (retainedFiles <= 0) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
            _path = path;
            _categoryName = categoryName;
            _maxFileBytes = maxFileBytes;
            _retainedFiles = retainedFiles;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var levelName = Enum.GetName(typeof(LogLevel), logLevel);
            var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {levelName} | {_categoryName}[{eventId.Id}]\n\t{ formatter(state, exception)}";

            lock (Locker)
            {
                var text = message + Environment.NewLine;
                var length = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                // Keep even an oversized diagnostic intact. Rotate before the next
                // message, never truncate or split a problem journal record.
                if (length > 0 && (length >= _maxFileBytes || Encoding.UTF8.GetByteCount(text) > _maxFileBytes - length))
                {
                    var oldest = _path + "." + _retainedFiles;
                    if (File.Exists(oldest)) File.Delete(oldest);
                    for (var index = _retainedFiles - 1; index >= 1; index--)
                    {
                        var previous = _path + "." + index;
                        if (File.Exists(previous)) File.Move(previous, _path + "." + (index + 1));
                    }
                    File.Move(_path, _path + ".1");
                }
                File.AppendAllText(_path, text);
            }
        }
    }
}
