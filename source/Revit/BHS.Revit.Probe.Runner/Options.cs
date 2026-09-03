using BHS.Shared;

namespace BHS.Revit.Probe.Runner;

/// <summary>What the command line asked for.</summary>
internal sealed class Options
{
    /// <summary>Releases to sweep, or empty for every one installed.</summary>
    public List<RevitRelease> Releases { get; } = new();

    /// <summary>Build the probe and install it into the per-user add-in folder first.</summary>
    public bool Deploy { get; set; }

    /// <summary>Remove the installed probe and stop. Nothing is launched.</summary>
    public bool Undeploy { get; set; }

    /// <summary>Leave Revit running after the checks, for a look by hand.</summary>
    public bool KeepOpen { get; set; }

    /// <summary>
    /// Launch even when the probe is not recorded as trusted for a release.
    /// </summary>
    /// <remarks>
    /// For the case the guard exists to prevent, done deliberately: somebody is sitting in front
    /// of the machine and will answer Revit's unsigned-add-in dialog with "Always load", which
    /// records the trust in whatever way that release actually wants. Useful on a release where
    /// writing the registry value turns out not to be enough - Revit 2024, as measured.
    /// </remarks>
    public bool AllowUntrusted { get; set; }

    /// <summary>
    /// How long to wait for a Revit to register.
    /// </summary>
    /// <remarks>
    /// Measured cold starts across the four releases run from 53 to 72 seconds to a usable main
    /// window, so the default is roughly three times the worst of them. The wait is not really
    /// governed by this: the process handle is watched, and a Revit that dies fails the check at
    /// once rather than at the deadline.
    /// </remarks>
    public TimeSpan RegistrationTimeout { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// How long to wait for Revit to close.
    /// </summary>
    /// <remarks>
    /// Generous because two waits are stacked inside it. The probe registers long before Revit is
    /// idle - 18s against about 72s to a usable window on 2024 - so the external event carrying
    /// the exit command sits in the queue until startup finishes; measured at 25 seconds on 2024.
    /// Only then does the teardown itself begin, and that alone was 9s on 2025 and 70s on 2027.
    /// A tighter budget does not fail the close, it just kills Revit in the middle of one.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(240);

    public static Options? Parse(string[] args)
    {
        var options = new Options();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--release" when index + 1 < args.Length && RevitRelease.TryParse(args[index + 1], out var release):
                    options.Releases.Add(release);
                    index++;
                    break;

                case "--deploy":
                    options.Deploy = true;
                    break;

                case "--undeploy":
                    options.Undeploy = true;
                    break;

                case "--keep-open":
                    options.KeepOpen = true;
                    break;

                case "--allow-untrusted":
                    options.AllowUntrusted = true;
                    break;

                case "--timeout" when index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds):
                    options.RegistrationTimeout = TimeSpan.FromSeconds(seconds);
                    index++;
                    break;

                default:
                    return null;
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Starts each installed Revit, waits for the probe add-in to register over the
            well-known pipe, asks it what it sees, and closes Revit from the inside.

            Options:
              --release <year>   only this release; may be repeated. Default: every one installed.
              --deploy           build the probe and install it into %AppData% first.
              --undeploy         remove the installed probe and stop.
              --keep-open        leave Revit running after the checks.
              --allow-untrusted  launch even when an installed add-in is unsigned and untrusted;
                                 you will have to answer Revit's dialogs by hand.
              --timeout <sec>    how long to wait for a registration. Default 240.

            The exit code is the number of failed checks.
            """);
    }
}
