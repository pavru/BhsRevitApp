using System.Globalization;
using BHS.Settings;

namespace BHS.Logging;

/// <summary>
/// Writes to the console. Win-side only, and never inside Revit, which has none.
/// </summary>
/// <remarks>
/// Worth saying what this is not. <c>BHS.WinSide</c> writes to the console in about thirty places
/// already, and almost all of that is the program's interface - the answer to <c>--status</c>,
/// <c>--settings</c>, <c>--launch</c>. Those must stay where they are. What moves here is only the
/// running commentary of the host: what arrived, what left, what went wrong.
/// <para>
/// Standard error for warnings and worse, so that piping the interface somewhere still leaves the
/// complaints visible.
/// </para>
/// </remarks>
public sealed class ConsoleLogSink : ILogSink, IConfigurableLogSink
{
    private readonly object _gate = new();

    public LogLevel Minimum { get; private set; } = LogLevel.Information;

    public void Configure(ISettings settings) =>
        Minimum = settings.Section("Log").Section("Console").Flag("Enabled", true)
            ? LogLevel.Trace
            : LogLevel.None;

    public void Emit(in LogEntry entry)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss} {1,-3} {2}",
            entry.Timestamp,
            entry.Level.ToString().Substring(0, 3).ToUpperInvariant(),
            entry.Message);

        var error = entry.Error;

        lock (_gate)
        {
            var writer = entry.Level >= LogLevel.Warning ? Console.Error : Console.Out;

            writer.WriteLine(line);

            if (error is not null)
                writer.WriteLine("    " + error.GetType().Name + ": " + error.Message);
        }
    }

    public void Dispose()
    {
    }
}
