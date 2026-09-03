namespace BHS.Logging;

/// <summary>
/// Somewhere a record ends up: a file, Revit's journal, the companion process, a debugger.
/// </summary>
/// <remarks>
/// A sink is called on whatever thread wrote the record, and there is no promise about which one
/// that is: inside Revit, calls arriving over the channel land on a pool thread while startup and
/// external events run on the API thread. A sink therefore has to be safe to call from several
/// threads at once, and must not block for long - the caller is doing real work.
/// <para>
/// A sink must not throw. A log that takes the process down with it is worse than no log, and
/// inside Revit it is somebody else's session. <see cref="LogRouter"/> catches anyway, because a
/// rule nothing enforces is a hope.
/// </para>
/// </remarks>
public interface ILogSink : IDisposable
{
    /// <summary>The lowest level this sink wants. Records below it are not offered.</summary>
    LogLevel Minimum { get; }

    void Emit(in LogEntry entry);
}
