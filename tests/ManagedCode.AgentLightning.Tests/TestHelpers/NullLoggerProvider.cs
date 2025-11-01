using Microsoft.Extensions.Logging;

namespace ManagedCode.AgentLightning.Tests.TestHelpers;

public sealed class NullLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new NullLogger();

    public void Dispose()
    {
    }

    private sealed class NullLogger : ILogger
    {
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
