using Microsoft.Extensions.Logging;

namespace L2Monitor.Core.Diagnostics;

public sealed class BoundedFileLoggerProvider : ILoggerProvider
{
    private readonly BoundedLogFile _logFile;

    public BoundedFileLoggerProvider(string filePath, int maxFileSizeBytes = 262_144, int retainedFileCount = 5)
    {
        _logFile = new BoundedLogFile(filePath, maxFileSizeBytes, retainedFileCount);
    }

    public ILogger CreateLogger(string categoryName) => new BoundedFileLogger(_logFile, categoryName);

    public void Dispose()
    {
    }

    private sealed class BoundedFileLogger(BoundedLogFile logFile, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            try
            {
                logFile.WriteLine(line);
            }
            catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
            {
                // The file sink is best-effort and must not destabilize runtime logging paths.
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
