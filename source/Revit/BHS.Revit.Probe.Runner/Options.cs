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

    /// <summary>
    /// Whether the edition is installed beside the probe, from the same build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It implies <see cref="Deploy"/>, because the point is that they cannot drift.</b>
    /// Installing the edition against a probe from some earlier build is the failure this flag
    /// exists to remove, so it is not a state the flag is allowed to produce.
    /// </para>
    /// <para>
    /// Off by default: the probe is an instrument and the edition is a product, and a measurement
    /// should not install a product into somebody's Revit as a side effect.
    /// </para>
    /// </remarks>
    public bool WithEdition { get; set; }

    /// <summary>Leave Revit running after the checks, for a look by hand.</summary>
    public bool KeepOpen { get; set; }

    /// <summary>
    /// Where to write the machine-readable record of this sweep, if anywhere.
    /// </summary>
    /// <remarks>
    /// The point of the file is that CI can check a sweep it cannot run: Revit needs an interactive
    /// session and a licence, so no hosted runner will ever start one. What CI verifies is the
    /// record - that it belongs to this commit, covers every release, contains no failure, and has
    /// not quietly lost a check since the last one. The file says so itself, in a field, because a
    /// disclaimer nobody reads is a disclaimer that eventually gets forgotten.
    /// </remarks>
    public string? ReportPath { get; set; }

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
    /// <para>
    /// Raised to 360 on the second reading that reached the old ceiling - 208s on 2024, then a 2026
    /// that had just spent 105 seconds opening a model and did not finish leaving inside four
    /// minutes. Both machines were on their fifth Revit of the hour, which is the condition a sweep
    /// creates for itself.
    /// </para>
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(360);

    /// <summary>
    /// Open a model on start, and check that the probe sees it.
    /// </summary>
    /// <remarks>
    /// Off by default, because it costs time and most of what the sweep checks needs no document.
    /// With it, the sweep exercises the half of registration that has no other test: registration
    /// happens in <c>OnStartup</c> where there is no document yet, and what a document is comes
    /// second, through <c>DocumentOpened</c>. The two have different budgets and only this proves
    /// the second one arrives at all.
    /// <para>
    /// A model belongs to a release: opening a 2024 file in 2027 upgrades it and writes it back.
    /// So the sweep takes a copy per run rather than opening the file in <c>testdata</c>, and picks
    /// the copy matching the release it is starting.
    /// </para>
    /// </remarks>
    public bool WithModel { get; set; }

    /// <summary>Open the linked set from <c>testdata/linked</c> rather than the empty model.</summary>
    /// <remarks>
    /// <para>
    /// <b>A folder, not a file, and that is the owner's instruction rather than a detail.</b> The set
    /// is three models - the electrical one that is opened, and the architecture and cabling-network
    /// models it links. Revit finds a link by the path it stored, relative to the host; copy the host
    /// alone, or copy the three under new names, and the links do not load and the host opens as a
    /// building with no walls and no carriers in it. So the whole folder is copied into a fresh
    /// directory with every file name kept.
    /// </para>
    /// <para>
    /// A separate switch rather than what <c>--with-model</c> does by default, for now. It changes
    /// what the recorded sweep is about - the empty model asserts nothing about carriers, this one
    /// does - and which one is canonical is the owner's to say once it has been measured.
    /// </para>
    /// </remarks>
    public bool Linked { get; set; }

    /// <summary>The model of the linked set that is opened; the others are what it links.</summary>
    /// <remarks>
    /// Named here rather than discovered, because nothing outside Revit can tell which of three
    /// models links the other two - and opening the wrong one would look like a model without links
    /// rather than like a mistake.
    /// </remarks>
    public const string LinkedHost = "TestCabling-ЭОМ.rvt";

    /// <summary>A model to open instead of the empty one from testdata.</summary>
    /// <remarks>
    /// For looking at a real project rather than the synthetic one the sweep normally uses. Real
    /// models are hundreds of megabytes and belong to somebody, so this takes a path and never a
    /// copy in the repository - and nothing derived from one is written into the recorded sweep
    /// beyond counts, because that record is public.
    /// </remarks>
    public string ModelPath { get; set; } = string.Empty;

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

                case "--edition":
                    options.WithEdition = true;
                    options.Deploy = true;
                    break;

                case "--deploy":
                    options.Deploy = true;
                    break;

                case "--undeploy":
                    options.Undeploy = true;
                    break;

                case "--model" when index + 1 < args.Length:
                    options.ModelPath = args[index + 1];
                    options.WithModel = true;
                    index++;
                    break;

                case "--with-model":
                    options.WithModel = true;
                    break;

                case "--linked":
                    options.Linked = true;
                    options.WithModel = true;
                    break;

                case "--report" when index + 1 < args.Length:
                    options.ReportPath = args[index + 1];
                    index++;
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
              --edition          install the edition too, from the same build. Implies --deploy,
                                 because two add-ins installed at different moments share the
                                 framework's simple names and the older copy wins.
              --undeploy         remove the installed probe and the edition, and stop.
              --with-model       open a model from testdata and check the probe reports it.
              --linked           open the linked set from testdata/linked - a host and its links,
                                 copied as one folder so the links still resolve.
              --model <path>     open this model instead of the one from testdata.
              --keep-open        leave Revit running after the checks.
              --allow-untrusted  launch even when an installed add-in is unsigned and untrusted;
                                 you will have to answer Revit's dialogs by hand.
              --timeout <sec>    how long to wait for a registration. Default 240.
              --report <path>    write the machine-readable record CI verifies, e.g.
                                 evidence/sweep-report.json. Commit the code first: the record
                                 refuses to claim a clean tree it did not have.

            The exit code is the number of failed checks.
            """);
    }
}
