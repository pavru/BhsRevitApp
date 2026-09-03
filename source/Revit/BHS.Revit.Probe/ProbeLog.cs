using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BHS.Revit.Probe;

/// <summary>
/// A log file, because inside Revit there is no console and a failure that leaves no trace is a
/// failure nobody can explain.
/// </summary>
/// <remarks>
/// Written eagerly and flushed on every line: the interesting failures are the ones where the
/// process is about to be killed by the runner, and a buffered log would lose exactly those.
/// </remarks>
internal static class ProbeLog
{
    private static readonly object Gate = new();

    /// <summary>Where this process writes. Reported to the runner so a failure can be read.</summary>
    public static string Path { get; } = BuildPath();

    public static void Write(string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff} [{1,2}] {2}",
            DateTime.Now,
            Environment.CurrentManagedThreadId,
            message);

        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // A log that cannot be written must not take the add-in down with it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Write(string message, Exception error) =>
        Write(message + ": " + error.GetType().Name + ": " + error.Message);

    /// <remarks>
    /// Under LocalApplicationData rather than the temp directory, and measured rather than assumed:
    /// Revit gives each session a temp folder of its own with a GUID in the name, so a log written
    /// there cannot be found from outside by a runner that never got a registration - which is
    /// precisely the case the log exists for.
    /// </remarks>
    private static string BuildPath()
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BHS.Revit.Probe");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException)
        {
        }

        var pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(directory, "probe." + pid + ".log");
    }
}
