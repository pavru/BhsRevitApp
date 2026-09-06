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

    public int Failures => _failed.Count;

    /// <summary>How many checks were reported, whatever their outcome.</summary>
    public int Performed { get; private set; }

    public bool Check(string what, bool ok)
    {
        Performed++;
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");

        if (!ok)
            _failed.Add(what);

        return ok;
    }

    /// <summary>A measurement rather than a verdict: something worth knowing that cannot fail.</summary>
    public static void Note(string what, string value) => Console.WriteLine($"       {what}: {value}");

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
