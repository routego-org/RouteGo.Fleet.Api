using Microsoft.Extensions.Logging;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>One captured log line.</summary>
/// <param name="Level">Its level.</param>
/// <param name="Message">The rendered message.</param>
public sealed record CapturedLog(LogLevel Level, string Message);

/// <summary>
/// Keeps every log line so a test can assert on alerts. Also serves as the logger factory and
/// provider for code that creates its own loggers, so every line from that code lands here too.
/// </summary>
public sealed class CapturingLogger : ILogger, ILoggerFactory, ILoggerProvider
{
    private readonly List<CapturedLog> _entries = new List<CapturedLog>();
    private readonly object _entriesGate = new object();

    /// <summary>Every captured line, in order.</summary>
    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            lock (_entriesGate)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entriesGate)
        {
            _entries.Add(new CapturedLog(logLevel, formatter(state, exception)));
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return this;
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
