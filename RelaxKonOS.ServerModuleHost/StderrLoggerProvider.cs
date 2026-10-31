using Microsoft.Extensions.Logging;

namespace RelaxKonOS.ServerModuleHost;

/// <summary>Module logs go to stderr so the stdout line protocol stays machine-readable.</summary>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            lock (Console.Error)
                Console.Error.WriteLine("[" + logLevel + "] " + category + ": " + message
                    + (exception is null ? "" : " " + exception.Message));
        }
    }
}
