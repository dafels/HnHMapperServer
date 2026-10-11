using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Web.Tests;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Records every log call (thread-safe) so tests can assert levels and counts.</summary>
public sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IReadOnlyList<LogEntry> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
