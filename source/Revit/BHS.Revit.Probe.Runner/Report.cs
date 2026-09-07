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

    /// <summary>Nothing reported after this belongs to a release.</summary>
    public void EndRelease() => _release = null;

    /// <summary>
    /// Says that a release stopped early, so its missing checks are explained.
    /// </summary>
    /// <remarks>
    /// The floor exists to catch a check that quietly stopped running. A release that gave up -
    /// Revit never opened its model, or stopped answering - is a different thing entirely, and it
    /// has already failed loudly by the time this is called. Counting it against the floor as well
    /// adds a second failure that says nothing new, on top of a real one. Measured: a deliberately
    /// broken model produced three failures where two were the story.
    ///
    /// <b>It marks the release, and does not close it.</b> The first version called
    /// <see cref="EndRelease"/>, which detached the record - and the cleanup that runs afterwards
    /// still reports: a Revit that refused to be killed printed a failing check that counted in the
    /// totals and appeared in no release, contradicting the one thing this record promises, that
    /// every line printed is a line recorded.
    /// </remarks>
    public void AbandonRelease()
    {
        if (_release is not null)
            _release.Abandoned = true;
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
    /// Repeated labels are kept, not merged. Several call sites print one line per settings layer
    /// or per assembly Revit shadowed, all under one label, so keeping the last would throw away
    /// precisely the detail worth recording.
    /// </remarks>
    public void Note(string what, string value)
    {
        Console.WriteLine($"       {what}: {value}");
        _release?.Notes.Add(new NoteRecord(what, value));
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

        // Each release answers for itself, and that is a correction. One global total against a
        // floor that dropped per abandoned release let the two cancel: an abandoned release keeps
        // the thirty-odd checks it did run in the total while removing forty-five from the bar, so
        // with two of four abandoned a completed release could lose nearly every check after
        // registration and still clear it. The guard exists for exactly that disappearance.
        foreach (var record in Sweep.Releases)
        {
            if (record.Abandoned || record.Checks.Count >= MinimumPerRelease)
                continue;

            // A symptom, not a diagnosis. The count also drops when a release aborts early - Revit
            // failing to register, say - and this repository has a documented history of chasing
            // wrong diagnoses printed by its own sweep. Naming both causes costs one line and stops
            // the next person from looking for a deleted check that was never deleted.
            Console.WriteLine(
                $"  [FAIL] Revit {record.Release} reported only {record.Checks.Count} checks, fewer " +
                $"than the {MinimumPerRelease} expected. Either a check stopped running, or that " +
                "release did not get far enough to ask its questions - the failures above say which.");
            _failed.Add($"Revit {record.Release} ran {record.Checks.Count} checks, fewer than the {MinimumPerRelease} expected");
        }

        // A release selected and never begun leaves no record at all, so the loop above cannot see
        // it. That is the one thing the old global count did catch, and it is kept.
        if (Sweep.Releases.Count < releases)
        {
            Console.WriteLine(
                $"  [FAIL] {releases} release(s) were selected but only {Sweep.Releases.Count} " +
                "reported anything at all.");
            _failed.Add($"{releases - Sweep.Releases.Count} selected release(s) reported nothing");
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
