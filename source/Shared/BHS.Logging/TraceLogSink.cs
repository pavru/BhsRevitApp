using System.Globalization;
using BHS.Settings;

namespace BHS.Logging;

/// <summary>Writes to the debugger's output window, and to DebugView when one is watching.</summary>
/// <remarks>
/// <c>Trace.WriteLine</c> and deliberately not <c>Debug.WriteLine</c>: the latter is compiled out
/// of our own Release build, which is exactly the build somebody attaches a debugger to when a
/// customer has a problem.
/// <para>
/// On by default only when a debugger is attached. Left on always it would cost a formatted string
/// per record for an audience that is not there.
/// </para>
/// </remarks>
public sealed class TraceLogSink : ILogSink, IConfigurableLogSink
{
    public LogLevel Minimum { get; private set; } =
        System.Diagnostics.Debugger.IsAttached ? LogLevel.Trace : LogLevel.None;

    public void Configure(ISettings settings)
    {
        var value = settings.Section("Log").Section("Trace")["Enabled"];

        var enabled = string.IsNullOrEmpty(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            ? System.Diagnostics.Debugger.IsAttached
            : settings.Section("Log").Section("Trace").Flag("Enabled", System.Diagnostics.Debugger.IsAttached);

        Minimum = enabled ? LogLevel.Trace : LogLevel.None;
    }

    public void Emit(in LogEntry entry) =>
        System.Diagnostics.Trace.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff} {1} [{2}] {3}: {4}{5}",
            entry.Timestamp,
            entry.Level,
            entry.ThreadId,
            entry.Category,
            entry.Message,
            entry.Error is null ? string.Empty : "  " + entry.Error));

    public void Dispose()
    {
    }
}
