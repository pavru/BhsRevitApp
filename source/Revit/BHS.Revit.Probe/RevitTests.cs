using BHS.Revit.Abstractions;
using BHS.Revit.Testing;

namespace BHS.Revit.Probe;

/// <summary>
/// The suites this sweep runs, listed by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list, never a scan.</b> The same rule as feature modules, for the same measured reason: on
/// Revit 2024 every add-in shares one AppDomain, and an assembly loaded in order to be looked at
/// holds its simple name there for the rest of the session. A scan would load every assembly beside
/// this one to ask whether it had tests in it.
/// </para>
/// <para>
/// <b>Why the probe hosts them.</b> It already is a test host - it answers a couple of hundred
/// checks - and it is the only add-in the runner deploys, drives and reads results from. A second
/// add-in with a channel of its own would race it for one pipe name; a second add-in without one
/// would have to write its results to a file, and then the watchdog that makes an unattended sweep
/// survivable would not be watching the thing under test.
/// </para>
/// <para>
/// <b>What it costs, stated rather than discovered later.</b> The probe now carries the cabling
/// assemblies into Revit, so the "assemblies in process" figures the sweep notes will be higher than
/// the ones recorded before this. That number is a note, not a check, and the metric this repository
/// actually defends - assemblies somebody else might also ship - is unchanged: every assembly added
/// here is ours and carries the BHS. prefix.
/// </para>
/// </remarks>
internal static class RevitTests
{
    /// <summary>Every suite, in the order they are reported.</summary>
    private static readonly IRevitTestSuite[] Declared =
    {
        new BHS.MEP.Cabling.Revit.Tests.CablingReadingTests(),
    };

    /// <summary>How many suites were declared, whatever happens when they run.</summary>
    /// <remarks>
    /// Answered from the array rather than from the results, so that a suite which failed to
    /// construct or list itself is visible as a difference between the two. A run that reported
    /// fewer suites than were declared looks, from the outside, exactly like a smaller run.
    /// </remarks>
    public static int SuiteCount => Declared.Length;

    /// <summary>Runs everything, on the API thread, and returns the results as the channel carries them.</summary>
    public static IReadOnlyDictionary<string, string> Measure(IRevitSession session)
    {
        var results = RevitTestRun.Execute(Declared, session.Application);
        var report = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in RevitTestRun.Flatten(results))
            report[pair.Key] = pair.Value;

        report["tests:suites"] = SuiteCount.ToString();
        return report;
    }
}
