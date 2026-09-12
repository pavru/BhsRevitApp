using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using BHS.Revit.Launch;
using BHS.Transport;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BHS.Revit.Probe.Runner;

internal static class Program
{
    internal static readonly string CurrentUser = WindowsIdentity.GetCurrent().Name;

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Options.PrintUsage();
            return 2;
        }

        var installed = RevitInstallation.Discover();
        if (installed.Count == 0)
        {
            Console.WriteLine(@"No Revit found under Program Files\Autodesk.");
            return 1;
        }

        if (options.Undeploy)
        {
            ProbeInstaller.Undeploy(installed);

            // The edition too, always. Removing one and leaving the other is exactly the drift
            // --edition exists to prevent, and a stale edition beside a fresh probe is worse than
            // either alone.
            EditionInstaller.Undeploy(installed);
            return 0;
        }

        if (options.Deploy && !TryDeploy(installed, options))
            return 1;

        var selected = SelectReleases(installed, options);
        if (selected.Count == 0)
            return 1;

        if (!GuardMachineIsFree(selected))
            return 1;

        // What is actually installed for each release about to be swept - not what was just built.
        var deployed = new Dictionary<int, DeployedProbe>();
        foreach (var installation in selected)
            deployed[installation.Release.Year] = ProbeInstaller.Describe(installation);

        var report = new Report();

        using var registry = new RevitInstanceRegistry();
        var channel = new RunnerChannel(registry);

        using var server = PipeTransport.CreateServer(PipeNames.WinSide);
        server.Error += (_, error) => Console.WriteLine($"       win-side server error: {error.Error.Message}");
        WinSideChannel.BindService(server.ServiceBinder, channel);
        server.Start();

        Console.WriteLine($"serving {PipeNames.WinSide} as {CurrentUser}");

        var launcher = new RevitLauncher(registry);

        DiscardStrayModelCopies();

        try
        {
            foreach (var installation in selected)
                await RunAsync(installation, launcher, registry, options, report,
                    deployed.TryGetValue(installation.Release.Year, out var probe) ? probe : null);
        }
        finally
        {
            server.Kill();
        }

        // The releases are done, so nothing from here belongs to one. Without this the leftovers
        // below were filed under whichever release happened to run last - Revit 2027, always,
        // whichever one actually leaked the instance.
        report.EndRelease();

        // Anything still here either was never ours or never left, and the two are worth telling
        // apart: a stranger is somebody's own Revit, a leftover is a registry that missed a death.
        foreach (var left in registry.Instances)
        {
            report.Note(
                left.StartedByUs ? "still registered after the sweep" : "another Revit registered without a token of ours",
                $"release {left.Release}, pid {left.ProcessId} - left alone");
        }

        report.Summarise(selected.Count);

        if (options.ReportPath is { Length: > 0 } reportPath)
        {
            var root = ProbeInstaller.FindRepositoryRoot() ?? Environment.CurrentDirectory;
            var (commit, clean, content) = SweepReport.DescribeWorkingTree(root, SweepReport.RevitSidePaths);

            report.Sweep.Commit = commit;
            report.Sweep.CommitClean = clean;
            report.Sweep.Content = content;
            report.Sweep.RecordedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            report.Sweep.WithModel = options.WithModel;
            report.Sweep.Linked = options.Linked;
            // Both conditions, because the ribbon is only exercised when both hold - the check
            // itself asks for WithModel as well. Recording the environment variable alone would
            // claim "ribbon exercised" for a run that skipped it, and worse: the verifier compares
            // check lists only between reports of the same mode, so a false flag would declare two
            // identical lists incomparable and switch the disappearing-check guard off.
            report.Sweep.ShowTab =
                options.WithModel && Environment.GetEnvironmentVariable("BHS_PROBE_SHOW_TAB") == "1";
            report.Sweep.Write(reportPath);
        }

        return report.Failures;
    }

    /// <summary>One release: start it, drive it, close it.</summary>
    /// <remarks>
    /// Starting, waiting and closing all belong to <see cref="RevitLauncher"/> now. They were
    /// written here first and moved out once they worked, which is why the sweep is still their
    /// test: whatever Win-side eventually does with them, this exercises them against four live
    /// Revit releases.
    /// </remarks>
    private static async Task RunAsync(
        RevitInstallation installation,
        RevitLauncher launcher,
        RevitInstanceRegistry registry,
        Options options,
        Report report,
        DeployedProbe? deployed)
    {
        report.BeginRelease(installation.Release.Year, deployed);
        Report.Heading($"Revit {installation.Release} - {installation.ExecutablePath}");

        var model = options.WithModel ? TakeModelCopy(installation, options, report) : null;

        using var session = await launcher.LaunchAsync(
            installation,
            new RevitLaunchOptions
            {
                RegistrationTimeout = options.RegistrationTimeout,
                ShutdownTimeout = options.ShutdownTimeout,
                ModelPath = model,
            });

        if (session.Process is not null)
            report.Note("launched", "pid " + session.Process.Id.ToString(CultureInfo.InvariantCulture));

        try
        {
            if (!report.Check("the add-in registers over the well-known pipe", session.Registered))
            {
                ExplainFailedLaunch(session, report);
                return;
            }

            report.Note("registered after", session.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

            await SweepChecks.InspectAsync(installation, registry, session, options, model, report);
        }
        finally
        {
            Finish(session, options, report);
            await DiscardModelCopyAsync(model);
        }
    }

    /// <summary>Removes this release's working copy, once Revit has let go of it.</summary>
    /// <remarks>
    /// <para>
    /// It used to be deleted at the end of the document check, which is to say while Revit still had
    /// the model open - so the delete failed, the exception was swallowed as "Revit still has it
    /// open; the temp directory keeps it", and the copy stayed forever. Not on the failing path
    /// only: on every path, because Revit holds the document until it exits. Measured on this
    /// machine, 51 copies and 3.9 GB, one per release per sweep since the day the option was added.
    /// </para>
    /// <para>
    /// So it belongs here, after <see cref="Finish"/> has closed or killed the process. The handle
    /// does not always come back the instant the process does, hence a few short attempts; and
    /// <c>--keep-open</c> is asked for deliberately, so a copy that stays behind it is not a leak.
    /// Whatever is still left is caught by <see cref="DiscardStrayModelCopies"/> next time.
    /// </para>
    /// </remarks>
    private static async Task DiscardModelCopyAsync(string? model)
    {
        if (model is null)
            return;

        // A linked set lives in a directory of its own, and the whole directory goes: deleting the
        // host alone would leave its links behind, one set per release per sweep.
        var directory = Path.GetDirectoryName(model);
        var ownDirectory = directory is not null
                           && Path.GetFileName(directory).StartsWith("bhs-sweep-", StringComparison.Ordinal);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (ownDirectory)
                    Directory.Delete(directory!, recursive: true);
                else
                    File.Delete(model);

                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Clears working copies an earlier sweep could not.</summary>
    /// <remarks>
    /// A sweep killed part way - and this one kills a Revit that will not open its model - never
    /// reaches its own cleanup, so the copy outlives it. Tidying at the start rather than at the end
    /// is the only placement that survives being killed, which is the case that leaks.
    ///
    /// Only files this runner makes, by its own prefix, and only ones older than an hour: a copy
    /// still held by a Revit refuses to be deleted anyway, and the age keeps a second sweep from
    /// tidying away the first one's work while it is using it.
    /// </remarks>
    private static void DiscardStrayModelCopies()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var removed = 0;
        var left = 0;

        try
        {
            foreach (var stray in Directory.EnumerateFiles(Path.GetTempPath(), "bhs-sweep-*.rvt"))
            {
                if (File.GetLastWriteTimeUtc(stray) > cutoff)
                    continue;

                try
                {
                    File.Delete(stray);
                    removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }

            // The linked sets, one directory each - they leak the same way and are twice the size.
            foreach (var stray in Directory.EnumerateDirectories(Path.GetTempPath(), "bhs-sweep-*"))
            {
                if (Directory.GetLastWriteTimeUtc(stray) > cutoff)
                    continue;

                try
                {
                    Directory.Delete(stray, recursive: true);
                    removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (removed > 0 || left > 0)
            Console.WriteLine($"cleared {removed} model copy(s) left by an earlier sweep"
                              + (left > 0 ? $", {left} still in use" : string.Empty));
    }

    /// <summary>Says what to look at when a Revit never announced itself.</summary>
    /// <remarks>
    /// The one path the runner has to explain without being told anything, because a probe that
    /// never registered never got to tell it anything.
    /// </remarks>
    private static void ExplainFailedLaunch(RevitSession session, Report report)
    {
        switch (session.Outcome)
        {
            case RevitLaunchOutcome.NotStarted:
                report.Note("Revit would not start at all", session.Installation.ExecutablePath);
                break;

            case RevitLaunchOutcome.ExitedBeforeRegistering:
                report.Note("Revit exited before registering", "exit code " + ExitCode(session));
                break;

            default:
                report.Note("probe log", SweepChecks.ProbeLogPath(session.Process?.Id ?? 0));
                report.Note("no log there means", "the add-in was never loaded - check the Revit journal");
                break;
        }
    }

    private static string ExitCode(RevitSession session)
    {
        try
        {
            return session.Process?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// A copy of the release's empty model, for this run only.
    /// </summary>
    /// <remarks>
    /// A copy, because opening a model can rewrite it - a file from an older release is upgraded on
    /// open - and the originals in <c>testdata</c> should survive being used. Per release, because
    /// which release a model belongs to is not a detail: giving 2027 the 2024 file would upgrade it
    /// and prove nothing about opening.
    /// </remarks>
    private static string? TakeModelCopy(RevitInstallation installation, Options options, Report report)
    {
        var year = installation.Release.Year.ToString(CultureInfo.InvariantCulture);

        // A model named on the command line is somebody's real project: hundreds of megabytes,
        // outside the repository, and not ours to alter. It is still copied rather than opened in
        // place, for the same reason the synthetic one is - the probe writes into the document to
        // check the settings path, and a group that rolls back is a promise rather than a proof.
        if (options.ModelPath.Length > 0)
        {
            if (!File.Exists(options.ModelPath))
            {
                report.Check("the model named on the command line exists", false);
                report.Note("looked for", options.ModelPath);
                return null;
            }

            return CopyForRun(options.ModelPath, year, report);
        }

        var root = ProbeInstaller.FindRepositoryRoot();

        if (root is null)
        {
            report.Check($"a test model for Revit {year} is available", false);
            return null;
        }

        if (options.Linked)
            return CopyLinkedSetForRun(Path.Combine(root, "testdata", "linked", year), year, report);

        var source = Path.Combine(root, "testdata", $"Empty Revit Model {year}.rvt");

        if (!File.Exists(source))
        {
            report.Check($"a test model for Revit {year} is available", false);
            report.Note("expected at", source);
            report.Note("how to make one", "see testdata/readme.md - they are deliberately not in git");
            return null;
        }

        return CopyForRun(source, year, report);
    }

    /// <summary>
    /// The whole linked set, in a directory of its own, with every file name kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's instruction, and the reason is how Revit finds a link.</b> The host stores the
    /// path of each link relative to itself; the single-file copy this runner makes of the empty
    /// model renames it to <c>bhs-sweep-...rvt</c> and puts it beside nothing, and a host treated
    /// that way opens with every link unresolved. So the folder is copied as it is, into
    /// <c>bhs-sweep-&lt;year&gt;-&lt;guid&gt;</c>, and the host is opened from inside it.
    /// </para>
    /// <para>
    /// Still a copy, for the reason every model here is one: opening can rewrite a file, and the
    /// probe writes into the document to check its paths. A directory per run, because two releases
    /// must never share one - the first to close would leave the second its links half deleted.
    /// </para>
    /// </remarks>
    private static string? CopyLinkedSetForRun(string source, string year, Report report)
    {
        var host = Path.Combine(source, Options.LinkedHost);

        if (!File.Exists(host))
        {
            report.Check($"the linked test set for Revit {year} is available", false);
            report.Note("expected at", host);
            report.Note("how to make one", "see testdata/readme.md - they are deliberately not in git");
            return null;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), $"bhs-sweep-{year}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var copied = 0;

            foreach (var file in Directory.EnumerateFiles(source, "*.rvt"))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: false);
                copied++;
            }

            // Counted rather than named. The file names are the owner's, and the record this lands
            // in is public; how many models came along is what a reader needs to tell "the links
            // were there" from "only the host was".
            report.Note("linked set", copied.ToString(CultureInfo.InvariantCulture) + " model(s), host and links copied together");
            return Path.Combine(directory, Options.LinkedHost);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Check($"the linked test set for Revit {year} could be copied", false);
            report.Note("why", error.Message);
            return null;
        }
    }

    private static string? CopyForRun(string source, string year, Report report)
    {
        try
        {
            var copy = Path.Combine(Path.GetTempPath(), $"bhs-sweep-{year}-{Guid.NewGuid():N}.rvt");
            File.Copy(source, copy, overwrite: true);
            return copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Check($"a test model for Revit {year} could be copied", false);
            report.Note("why", error.Message);
            return null;
        }
    }

    /// <summary>Kills a Revit that would otherwise be left behind holding a licence seat.</summary>
    private static void Finish(RevitSession session, Options options, Report report)
    {
        if (options.KeepOpen || !session.IsRunning)
            return;

        report.Note("killing", "pid " + (session.Process?.Id.ToString(CultureInfo.InvariantCulture) ?? "?")
                               + " - it did not close on its own");

        report.Check("the leftover Revit could be killed", session.Kill());
    }

    private static bool TryDeploy(IReadOnlyList<RevitInstallation> installed, Options options)
    {
        var root = ProbeInstaller.FindRepositoryRoot();

        if (root is null)
        {
            Console.WriteLine("--deploy needs the repository, and this build is not inside one.");
            return false;
        }

        if (!ProbeInstaller.Deploy(root, installed))
        {
            Console.WriteLine("The build failed, so nothing was installed.");
            return false;
        }

        // Which editions to build beside the probe: every release when asked for, and otherwise the
        // releases that already have one installed.
        //
        // The second half is a correction, and it was paid for with a whole sweep. Once the probe began
        // carrying the feature's own assemblies for the declared tests, the edition and the probe
        // started sharing the simple names BHS.MEP.Cabling.Revit and .Routing - and --deploy refreshed
        // only the probe. On Revit 2027 the edition's copy won the name, it came from an older tree,
        // and six test cases died on MissingMethodException and TypeLoadException for a type added an
        // hour before. The prediction was already written down in CLAUDE.md; the command still left it
        // to memory. Refreshing an edition that is already there is not installing a product into
        // somebody's Revit - it is keeping two deployments of one tree in step, which is the only
        // state in which the sweep's answer means anything.
        var editions = options.WithEdition
            ? installed
            : installed.Where(one => Directory.Exists(EditionInstaller.EditionDirectory(one))).ToList();

        if (editions.Count == 0)
            return true;

        if (!options.WithEdition)
        {
            Console.WriteLine(
                "the edition is installed beside the probe for "
                + string.Join(", ", editions.Select(one => one.Release.Year))
                + " and shares its assemblies' names, so it is rebuilt from this tree too");
        }

        if (!EditionInstaller.Deploy(root, editions))
        {
            // Loudly, and as a failure: the probe has just been refreshed, so a probe without the
            // edition it was meant to be installed beside is the drift this flag was asked for.
            Console.WriteLine("The edition did not build, and the probe has already been replaced.");
            Console.WriteLine("Deploy both again once it builds, or run --undeploy to clear both.");
            return false;
        }

        // Asked while both folders are still warm, because this is the one moment when the answer
        // is cheap to act on: the fix is to run the command again. The same question is asked from
        // inside Revit by FrameworkAssemblyCheck, but by then somebody is already debugging.
        EditionInstaller.ReportSharedAssemblies(editions);
        return true;
    }

    private static List<RevitInstallation> SelectReleases(IReadOnlyList<RevitInstallation> installed, Options options)
    {
        var selected = options.Releases.Count == 0
            ? installed.ToList()
            : installed.Where(installation => options.Releases.Contains(installation.Release)).ToList();

        foreach (var asked in options.Releases.Where(asked => selected.All(found => found.Release != asked)))
            Console.WriteLine($"Revit {asked} is not installed.");

        var missing = selected.Where(installation => !ProbeInstaller.IsDeployed(installation)).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine("The probe is not installed for: " + string.Join(", ", missing.Select(one => one.Release)));
            Console.WriteLine("Install it with --deploy, or by hand:");
            Console.WriteLine("  dotnet build " + ProbeDeployment.ProjectPath + " -c Release -p:RevitDeploy=Local");
            return new List<RevitInstallation>();
        }

        // Checked rather than assumed, because the failure is silent from out here: an unsigned
        // add-in Revit has not been told to trust brings up a modal dialog whose default answer is
        // "do not load", before any add-in gets its OnStartup. The sweep would wait out its whole
        // deadline in front of it and report that the probe never registered.
        //
        // Not only our own add-in. The dialog belongs to whichever add-in is untrusted, and a
        // machine somebody actually works on collects them.
        var blocked = selected
            .Select(installation => (Installation: installation, Untrusted: AddInTrust.Untrusted(installation)))
            .Where(pair => pair.Untrusted.Count > 0)
            .ToList();

        if (blocked.Count == 0)
            return selected;

        foreach (var (installation, untrusted) in blocked)
        {
            Console.WriteLine($"Revit {installation.Release} would stop and ask about:");

            foreach (var addIn in untrusted)
            {
                var ours = ProbeDeployment.AddInIds.Contains(addIn.AddInId, StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  {addIn}{(ours ? "  <- ours, run --deploy" : string.Empty)}");
            }
        }

        if (options.AllowUntrusted)
        {
            Console.WriteLine("continuing anyway - answer each dialog with \"Always load\" as it appears.");
            return selected;
        }

        Console.WriteLine();
        Console.WriteLine("Trust them in Revit (\"Always load\"), uninstall them, or pass --allow-untrusted");
        Console.WriteLine("to answer the dialogs by hand.");
        return new List<RevitInstallation>();
    }

    /// <summary>
    /// Refuses to run when a Revit is already open, or when someone else owns the well-known name.
    /// </summary>
    /// <remarks>
    /// Both are about not disturbing what is already there. The sweep kills what it started, and
    /// telling its own Revit from somebody's work in progress after the fact is not a game worth
    /// playing. The name matters for a different reason: the library never sets
    /// <c>FirstPipeInstance</c> - it does not exist on .NET Framework at all - so a second server
    /// on the same name would coexist silently and connections would land on either.
    /// </remarks>
    private static bool GuardMachineIsFree(IReadOnlyList<RevitInstallation> selected)
    {
        var running = Process.GetProcessesByName("Revit");

        if (running.Length > 0)
        {
            Console.WriteLine($"Revit is already running ({running.Length} process(es)). Close it first: the sweep starts and kills its own.");
            return false;
        }

        if (PipeNames.Enumerate().Contains(PipeNames.WinSide))
        {
            Console.WriteLine($"Something is already serving {PipeNames.WinSide}. Two servers on one name coexist silently, so this run would be unreliable.");
            return false;
        }

        Console.WriteLine("sweeping: " + string.Join(", ", selected.Select(one => one.Release)));
        return true;
    }
}
