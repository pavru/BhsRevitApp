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

    public bool Check(string what, bool ok)
    {
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

    public void Summarise()
    {
        Console.WriteLine();

        if (_failed.Count == 0)
        {
            Console.WriteLine("== all checks passed");
            return;
        }

        Console.WriteLine($"== {_failed.Count} check(s) FAILED");
        foreach (var failure in _failed)
            Console.WriteLine("   - " + failure);
    }
}
