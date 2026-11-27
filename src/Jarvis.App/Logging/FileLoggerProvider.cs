using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jarvis.App.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Jarvis log writer" };
        _writer.Start();
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _queue);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"jarvis-{DateTime.Now:yyyyMMdd}.log"), line);
            }
            catch (IOException)
            {
                // Logging must never crash the assistant.
            }
        }
    }

    private sealed class FileLogger(string category, BlockingCollection<string> queue) : ILogger
    {
        private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..4].ToUpperInvariant()}] {_category}: {formatter(state, exception)}" +
                       (exception is null ? string.Empty : Environment.NewLine + exception) + Environment.NewLine;
            queue.TryAdd(line);
        }
    }
}
