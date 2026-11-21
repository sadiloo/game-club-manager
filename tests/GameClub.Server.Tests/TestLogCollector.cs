using Microsoft.Extensions.Logging;

namespace GameClub.Server.Tests;

public sealed class TestLogCollector
{
    private readonly object _gate = new();
    public List<TestLogEntry> Entries { get; } = [];

    public void Clear()
    {
        lock (_gate)
        {
            Entries.Clear();
        }
    }

    internal void Add(TestLogEntry entry)
    {
        lock (_gate)
        {
            Entries.Add(entry);
        }
    }
}

public sealed record TestLogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

internal sealed class TestLogCollectorProvider(TestLogCollector collector) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestCollectorLogger(categoryName, collector);

    public void Dispose()
    {
    }
}

internal sealed class TestCollectorLogger(string categoryName, TestLogCollector collector) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        collector.Add(new TestLogEntry(logLevel, categoryName, formatter(state, exception), exception));
    }
}