namespace BHS.Logging;

/// <summary>One record, as a sink receives it.</summary>
/// <remarks>
/// A readonly struct passed by <c>in</c>: a sink is called on the writer's thread, once per record,
/// and there may be several sinks. Nothing here should allocate on the way.
/// </remarks>
public readonly struct LogEntry
{
    public LogEntry(
        DateTimeOffset timestamp,
        LogLevel level,
        string category,
        string message,
        Exception? error,
        int threadId,
        bool onPrimaryThread)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category;
        Message = message;
        Error = error;
        ThreadId = threadId;
        OnPrimaryThread = onPrimaryThread;
    }

    public DateTimeOffset Timestamp { get; }

    public LogLevel Level { get; }

    /// <summary>Who wrote it. Starts with the edition, so a shared file says whose line this is.</summary>
    public string Category { get; }

    public string Message { get; }

    public Exception? Error { get; }

    public int ThreadId { get; }

    /// <summary>
    /// Whether this was written on the host's primary thread - inside Revit, the API thread.
    /// </summary>
    /// <remarks>
    /// Recorded rather than deduced later, because by the time anyone reads the file the thread is
    /// gone. It is worth having: a call arriving over the channel lands on a pool thread, never on
    /// Revit's API thread, and telling the two apart in a log is how a threading mistake stops
    /// being a mystery. It is also what lets a sink that needs the API thread know it has one.
    /// </remarks>
    public bool OnPrimaryThread { get; }
}
