using System.Globalization;
using BHS.Settings;

namespace BHS.Logging;

/// <summary>
/// The standard arrangement of sinks, so that each host does not invent its own.
/// </summary>
/// <remarks>
/// The order here is the order the startup sequence needs, and it matters. Writing works from the
/// first line, before any of this runs, because <see cref="LogRouter.Default"/> opens a file by
/// itself; what this adds is everything that needs settings, and settings arrive later. Nothing
/// already written is replayed - it is in the same file already.
/// </remarks>
public static class LogSetup
{
    private static int _watching;

    /// <summary>
    /// Attaches the usual sinks, applies the settings, and keeps applying them as they change.
    /// </summary>
    /// <param name="router">Usually <see cref="LogRouter.Default"/>.</param>
    /// <param name="name">What this process is, for the log file name, when none is open yet.</param>
    /// <param name="settings">The layered settings; reapplied whenever a file changes.</param>
    /// <param name="console">Win-side only. Revit has no console to write to.</param>
    /// <param name="facts">Lines for the file header: who we are, and what got loaded.</param>
    /// <remarks>
    /// Safe to call twice, which on Revit 2024 is not hypothetical - two of our editions share one
    /// AppDomain and one router. A sink of a kind already attached is left alone rather than
    /// doubled, and the settings are simply applied again.
    /// </remarks>
    public static LogRouter Start(
        LogRouter router,
        string name,
        LayeredSettings settings,
        bool console = false,
        IEnumerable<KeyValuePair<string, string>>? facts = null)
    {
        if (router is null)
            throw new ArgumentNullException(nameof(router));
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var file = Existing<FileLogSink>(router)
                   ?? Attach(router, new FileLogSink(name, settings.Section("Log").Text("Directory")));

        if (Existing<TraceLogSink>(router) is null)
            router.Add(new TraceLogSink());

        if (console && Existing<ConsoleLogSink>(router) is null)
            router.Add(new ConsoleLogSink());

        router.Apply(settings);
        settings.Changed += (_, _) => router.Apply(settings);

        if (file is not null)
            file.WriteHeader(Header(name, facts));

        WatchLateLoads(router);

        // Off the startup path on purpose. Walking a directory of a few hundred files is quick, but
        // the budget it would join is the one that already reaches thirty seconds to registration on
        // Revit 2024, and housekeeping has no claim on any of it.
        var section = settings.Section("Log").Section("File");
        var directory = settings.Section("Log").Text("Directory");
        var retain = section.Number("Retain", FileLogSink.DefaultRetain);
        var days = section.Number("RetainDays", FileLogSink.DefaultRetainDays);

        System.Threading.ThreadPool.QueueUserWorkItem(_ => FileLogSink.Sweep(directory, retain, days));

        return router;
    }

    /// <summary>
    /// Records the assemblies that arrive after the header was written.
    /// </summary>
    /// <remarks>
    /// The header is a snapshot, and a snapshot taken early misses exactly what a framework loads
    /// on the way up - found the moment logging moved into the host, which raises it before the
    /// transport is touched at all, so the header stopped naming the assembly the question was
    /// usually about.
    /// <para>
    /// <c>AssemblyLoad</c> and emphatically not <c>AssemblyResolve</c>. On Revit 2024 this handler
    /// sees every vendor's loads, which is harmless because it only watches; a resolve handler in
    /// the same position would be answering for them, which is why one was removed from the probe.
    /// </para>
    /// </remarks>
    private static void WatchLateLoads(LogRouter router)
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) != 0)
            return;

        var log = router.For("BHS.Logging");

        AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
        {
            try
            {
                var name = args.LoadedAssembly.GetName();

                if (!LoadedAssemblies.IsWorthReporting(name.Name ?? string.Empty))
                    return;

                log.Info("assembly:{0} {1} | {2}", name.Name, name.Version, SafeLocation(args.LoadedAssembly));
            }
            catch (Exception)
            {
                // A log line is never worth an exception on somebody else's load.
            }
        };
    }

    private static string SafeLocation(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? "(dynamic)" : assembly.Location;
        }
        catch (NotSupportedException)
        {
            return "(no file)";
        }
    }

    /// <summary>What goes at the top of a log file.</summary>
    /// <remarks>
    /// The assembly list is the standing requirement to record which copy of each shared assembly
    /// actually got loaded, with version and path. It belongs in the header rather than in a file of
    /// its own, because the answer only means anything next to the run it describes - and because
    /// the two failures it exists to catch, <c>System.Memory</c> on Revit 2024 and
    /// <c>Microsoft.Extensions.Configuration</c> on 2026, both looked like something else entirely
    /// until somebody asked the running process this question.
    /// </remarks>
    private static IEnumerable<KeyValuePair<string, string>> Header(
        string name,
        IEnumerable<KeyValuePair<string, string>>? facts)
    {
        yield return new KeyValuePair<string, string>("started", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>("process", name);
        yield return new KeyValuePair<string, string>("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        if (facts is not null)
        {
            foreach (var fact in facts)
                yield return fact;
        }

        foreach (var pair in LoadedAssemblies.Report())
            yield return new KeyValuePair<string, string>(pair.Key, pair.Value);
    }

    private static T? Existing<T>(LogRouter router) where T : class, ILogSink
    {
        foreach (var sink in router.Sinks)
        {
            if (sink is T found)
                return found;
        }

        return null;
    }

    private static FileLogSink? Attach(LogRouter router, FileLogSink sink)
    {
        router.Add(sink);
        return sink;
    }
}
