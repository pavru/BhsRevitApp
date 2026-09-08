using System.Diagnostics;
using System.IO;
using System.Reflection;
using BHS.Logging;

namespace BHS.Revit.Host;

/// <summary>
/// Says out loud when a framework assembly came from somebody else's copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, at the first press of the first command this repository ever shipped:</b>
/// </para>
/// <code>
/// MissingMethodException: Method not found:
///   'Double BHS.Settings.SettingsValues.Real(BHS.Settings.ISettings, System.String, Double)'
/// </code>
/// <para>
/// The method was there - in the copy sitting in the edition's own folder. But the probe had been
/// deployed twenty minutes earlier, before that method existed, and its <c>BHS.Settings.dll</c> took
/// the simple name in the shared AppDomain first. This is the collision the whole repository is
/// built around, arrived at for the first time between two of our own add-ins.
/// </para>
/// <para>
/// <b>It cannot be fixed, only named.</b> Two add-ins ship the same framework assemblies under the
/// same simple names, and only one copy of each is ever loaded. What can be fixed is the shape of
/// the failure: a name, two versions and two paths at startup, rather than a missing method an hour
/// later inside a command.
/// </para>
/// <para>
/// <b>Who this is for, decided by the owner: us.</b> In production exactly one edition is ever
/// installed - switching editions uninstalls the previous one - so two of ours side by side is a
/// development state. That does not retire the check; it changes what a hit means. Here it catches
/// a deployment refreshed out of step with another, and in the field it catches an uninstall that
/// left a folder or a manifest behind, which is a real defect in an installation. Both deserve the
/// error level they get.
/// </para>
/// <para>
/// <b>The two axes fail differently, and both were measured with a deliberate mismatch:</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// Revit 2024, on .NET Framework: the second add-in <b>starts</b> - the journal says
/// <c>API_SUCCESS</c> - binds to the other one's framework, and this check reports four assemblies
/// by name. It is then running against a framework it was not built for, which is where a missing
/// method comes from.
/// </description></item>
/// <item><description>
/// Revit 2026, on .NET: the second add-in <b>does not start at all</b> - <c>API_ERROR</c> in the
/// journal, and nothing of ours runs. The default load context resolves by simple name, and a
/// request for a higher version than the one already loaded is refused outright. This check never
/// executes there, because the host it lives in never does.
/// </description></item>
/// </list>
/// <para>
/// <b>What this does not catch, said plainly: the case that produced it.</b> Both copies were
/// <c>0.1.0.0</c> and differed only in content, because one had been redeployed and the other had
/// not. Versions cannot tell those apart, and bytes cannot either - measured, two builds of
/// unchanged source do not produce identical files, so a hash would fire on every ordinary
/// deployment and be ignored within a week. The same-version case is therefore reported as an
/// <c>Info</c> line that names the foreign path and says what it would mean, which is the clue a
/// person needs when a method has gone missing.
/// </para>
/// </remarks>
internal static class FrameworkAssemblyCheck
{
    /// <summary>The prefix every assembly of ours carries.</summary>
    /// <remarks>
    /// The same prefix that keeps us from colliding with other vendors is what makes this check
    /// possible: an assembly named <c>BHS.*</c> is one we might ship, and one we do not ship is
    /// skipped by the file test below rather than by a list somebody has to maintain.
    /// </remarks>
    private const string Ours = "BHS.";

    /// <summary>
    /// Checks what is loaded now, and keeps checking what loads later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are needed and neither is enough. At startup most of our assemblies are not
    /// loaded yet - deliberately, since a feature's assembly must stay out of the AppDomain until
    /// its button is pressed - so a single sweep would see almost nothing. A subscription alone
    /// would miss everything the host itself pulled in on the way up.
    /// </para>
    /// <para>
    /// Two hosts in one AppDomain subscribe twice, and that is right rather than duplicated: each
    /// checks against its own folder, so the one that lost the race reports the collision and the
    /// one that won says nothing.
    /// </para>
    /// </remarks>
    public static void Watch(string ownDirectory, ILog log)
    {
        if (string.IsNullOrEmpty(ownDirectory) || log is null)
            return;

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            Inspect(loaded, ownDirectory, log);

        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Inspect(args.LoadedAssembly, ownDirectory, log);
    }

    private static void Inspect(Assembly assembly, string ownDirectory, ILog log)
    {
        try
        {
            var name = assembly.GetName().Name;

            if (name is null || !name.StartsWith(Ours, StringComparison.Ordinal))
                return;

            var mine = Path.Combine(ownDirectory, name + ".dll");

            // Not ours to worry about: we do not ship this one, so whoever did owns the question.
            if (!File.Exists(mine))
                return;

            var loadedFrom = Location(assembly);

            if (loadedFrom.Length == 0)
                return;

            // The ordinary case, and the silent one.
            if (string.Equals(loadedFrom, mine, StringComparison.OrdinalIgnoreCase))
                return;

            var theirs = FileVersion(loadedFrom);
            var ours = FileVersion(mine);

            if (string.Equals(theirs, ours, StringComparison.Ordinal))
            {
                // Not an error, and not nothing either - this is the line that answers the question
                // that actually got asked. The failure this check was written for had both copies at
                // the same version and different content, because only one of them had been
                // redeployed; the version cannot tell those apart, so the honest thing is to say
                // where the assembly came from and what that would mean.
                log.Info(
                    "assembly {0} {1} came from another add-in at {2} - if a method goes missing, one of the two deployments is stale",
                    name,
                    ours,
                    loadedFrom);
                return;
            }

            // Two lines rather than one, and not for width: ILog takes at most three arguments on
            // purpose, so that a disabled level costs no array and no boxing. A message that needs
            // five is a message that wants to be two.
            log.Error("assembly {0} came from another add-in: {1} at {2}", name, theirs, loadedFrom);

            log.Error(
                "  ours is {0} at {1} - deploy both from one tree, or anything this add-in calls that "
                + "the other copy lacks will fail with a missing method",
                ours,
                mine);
        }
        catch (Exception error)
        {
            // A diagnostic that can fail the thing it observes is worse than no diagnostic, and this
            // one runs inside somebody else's assembly load.
            log.Warn(error, "could not check where {0} was loaded from", SafeName(assembly));
        }
    }

    private static string Location(Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? string.Empty : assembly.Location ?? string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "(none)";
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }

    private static string SafeName(Assembly assembly)
    {
        try
        {
            return assembly.GetName().Name ?? "(unnamed)";
        }
        catch (Exception)
        {
            return "(unnamed)";
        }
    }
}
