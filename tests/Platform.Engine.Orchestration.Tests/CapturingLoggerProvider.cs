using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// A thread-safe <see cref="ILoggerProvider"/> that collects every log entry emitted
/// during a test so that tests can assert on log content.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<LogEntry> _entries = new();

    /// <summary>Gets all log entries captured since this provider was created.</summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, _entries);

    /// <inheritdoc />
    public void Dispose() { /* nothing to release */ }

    // ------------------------------------------------------------------
    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentBag<LogEntry> _sink;

        internal CapturingLogger(string category, ConcurrentBag<LogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Add(new LogEntry(_category, logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>A single captured log entry.</summary>
/// <param name="Category">The logger category (fully qualified type name).</param>
/// <param name="Level">The <see cref="LogLevel"/>.</param>
/// <param name="Message">The formatted log message.</param>
internal sealed record LogEntry(string Category, LogLevel Level, string Message);
