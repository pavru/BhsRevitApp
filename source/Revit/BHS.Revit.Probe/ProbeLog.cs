using BHS.Logging;

namespace BHS.Revit.Probe;

/// <summary>
/// The probe's own name for the framework log.
/// </summary>
/// <remarks>
/// It used to be a file writer of its own, written before there was a logging layer, and its
/// conclusions are the ones that layer was built on: write under <c>%LocalAppData%</c> rather than
/// Revit's session temp folder, flush every line, and never let a failed write take the add-in
/// down. Now it is eight lines over <see cref="Log"/>, which is the point - the probe should
/// exercise what a real add-in uses, not a private arrangement that could drift from it.
/// <para>
/// <see cref="Log.For(string)"/> works from the first line of <c>OnStartup</c>, before settings
/// have been read and before any host has configured anything, because the default router opens a
/// file by itself. That is exactly the window the probe's own logger existed to cover.
/// </para>
/// </remarks>
internal static class ProbeLog
{
    private static readonly ILog Sink = Log.For("BHS.Revit.Probe");

    /// <summary>Where this process is writing, so a failed run can still be read from outside.</summary>
    public static string Path
    {
        get
        {
            foreach (var sink in LogRouter.Default.Sinks)
            {
                if (sink is FileLogSink file)
                    return file.Path;
            }

            return string.Empty;
        }
    }

    public static void Write(string message) => Sink.Info(message);

    public static void Write(string message, Exception error) => Sink.Error(error, "{0}", message);
}
