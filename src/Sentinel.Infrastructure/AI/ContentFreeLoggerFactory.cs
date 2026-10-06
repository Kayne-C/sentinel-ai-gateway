using Microsoft.Extensions.Logging;

namespace Sentinel.Infrastructure.AI;

/// <summary>
/// The Microsoft.Extensions.AI logging middleware serialises full prompts and completions at Trace level. Prompts are
/// redacted by then, but they still carry document content, which must never end up in logs; loggers from this
/// factory therefore never report Trace as enabled, whatever the configured minimum level is. Debug and above
/// (invocations, durations, failures) pass through unchanged.
/// </summary>
internal sealed class ContentFreeLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ContentFreeLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    /// <summary>The wrapped factory belongs to the container.</summary>
    public void Dispose()
    {
    }

    private sealed class ContentFreeLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel > LogLevel.Trace && inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel > LogLevel.Trace)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
