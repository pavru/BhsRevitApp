namespace BHS.Revit.Probe.Runner;

/// <summary>
/// The running tally, printed as it goes.
/// </summary>
/// <remarks>
/// Printed line by line rather than collected and shown at the end, because a sweep takes about a
/// minute per release and a run that hangs should still have said what it managed to prove.
/// </remarks>
internal sealed class Report
{
    private readonly List<string> _failed = new();
    private ReleaseRecord? _release;

    /// <summary>
    /// The same sweep, in a form something other than a person can read.
    /// </summary>
    /// <remarks>
    /// Filled as the console output is written rather than assembled afterwards, so the two cannot
    /// disagree: every line printed is a line recorded. See SweepReport for why the record is data
    /// and not the raw log, and for the disclaimer it carries about what CI can and cannot do with
    /// it.
    /// </remarks>
    public SweepReport Sweep { get; } = new();

    /// <summary>Everything reported from here belongs to this Revit release.</summary>
    /// <remarks>
    /// The deployed build is recorded alongside the checks rather than beside them, because the
    /// question a reader has later is always about the pair: these results, from that assembly.
    /// A record of checks that does not say what was checked answers nothing.
    /// </remarks>
    public void BeginRelease(int release, DeployedProbe? deployed)
    {
        _release = new ReleaseRecord
        {
            Release = release,
            FileVersion = deployed?.FileVersion,
            BuiltUtc = deployed?.BuiltUtc,
            Signed = deployed?.Signed,
        };

        Sweep.Releases.Add(_release);
    }

    public int Failures => _failed.Count;

    /// <summary>How many checks were reported, whatever their outcome.</summary>
    public int Performed { get; private set; }

    public bool Check(string what, bool ok)
    {
        Performed++;
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        _release?.Checks.Add(new CheckRecord(what, ok));

        if (!ok)
            _failed.Add(what);

        return ok;
    }

    /// <summary>A measurement rather than a verdict: something worth knowing that cannot fail.</summary>
    /// <remarks>
    /// Recorded as well as printed, and this is where the sweep's numbers stop being anecdotes.
    /// The timings in CLAUDE.md were transcribed by hand and have misled three times - 208 s, 696 s
    /// and then 16 s for the same wait - because a single figure read off one run looks like a
    /// measurement. Collected per release and per run, the spread is visible as spread.
    ///
    /// A repeated key keeps the last value: a note asked twice in one release is a later
    /// measurement of the same thing, not a second thing.
    /// </remarks>
    public void Note(string what, string value)
    {
        Console.WriteLine($"       {what}: {value}");

        if (_release is not null)
            _release.Notes[what] = value;
    }

    public static void Heading(string text)
    {
        Console.WriteLine();
        Console.WriteLine("== " + text);
    }

    /// <summary>
    /// The fewest checks a single release is expected to contribute.
    /// </summary>
    /// <remarks>
    /// A floor, not a figure: adding checks is the normal direction and must not require an edit
    /// here. It exists for the one failure a sweep cannot otherwise see - a check that stopped
    /// running. A condition that is never evaluated prints nothing, fails nothing, and is
    /// indistinguishable from one that passed; that is how RefCheck went months unimported while
    /// the documentation called it a build failure, and how the placeholder icons passed every
    /// assertion while holding a quarter of the logo.
    ///
    /// Measured at 53 for a plain single-release sweep, so 45 leaves room for a mode that asks
    /// fewer questions without leaving room for a whole area to disappear.
    /// </remarks>
    public const int MinimumPerRelease = 45;

    public void Summarise(int releases)
    {
        Console.WriteLine();

        var floor = MinimumPerRelease * Math.Max(releases, 1);

        if (Performed < floor)
        {
            // A symptom, not a diagnosis. The count also drops when a release aborts early - Revit
            // failing to register, say - and this repository has a documented history of chasing
            // wrong diagnoses printed by its own sweep. Naming both causes costs one line and stops
            // the next person from looking for a deleted check that was never deleted.
            Console.WriteLine(
                $"  [FAIL] only {Performed} checks ran across {releases} release(s), fewer than the " +
                $"{floor} expected. Either a check stopped running, or a release did not get far " +
                "enough to ask its questions - the failures above say which.");
            _failed.Add($"the sweep ran {Performed} checks, fewer than the {floor} expected");
        }

        Sweep.Performed = Performed;
        Sweep.Failed = _failed.Count;

        if (_failed.Count == 0)
        {
            Console.WriteLine($"== all checks passed ({Performed})");
            return;
        }

        Console.WriteLine($"== {_failed.Count} check(s) FAILED, out of {Performed}");
        foreach (var failure in _failed)
            Console.WriteLine("   - " + failure);
    }
}
