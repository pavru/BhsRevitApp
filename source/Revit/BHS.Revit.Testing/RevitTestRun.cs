using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BHS.Revit.Testing;

/// <summary>How a case ended.</summary>
public enum RevitTestOutcome
{
    Passed,
    Failed,

    /// <summary>Not run, and the reason is recorded rather than left to be guessed at.</summary>
    Skipped,
}

/// <summary>One case, after the fact.</summary>
public sealed class RevitTestResult
{
    internal RevitTestResult(
        string suite,
        string name,
        RevitTestOutcome outcome,
        string detail,
        TimeSpan elapsed,
        IReadOnlyList<KeyValuePair<string, string>> notes)
    {
        Suite = suite;
        Name = name;
        Outcome = outcome;
        Detail = detail;
        Elapsed = elapsed;
        Notes = notes;
    }

    public string Suite { get; }

    public string Name { get; }

    public RevitTestOutcome Outcome { get; }

    /// <summary>Why it failed, or why it was skipped. Empty when it passed.</summary>
    public string Detail { get; }

    public TimeSpan Elapsed { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Notes { get; }
}

/// <summary>
/// Runs declared suites inside a live Revit, on the API thread, and says what happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case runs.</b> One that throws fails and the next one starts: a run that stopped at the
/// first failure would report one defect per sweep, and a sweep costs half an hour of somebody's
/// Revit. The same reason the sweep already reports every check rather than exiting on the first.
/// </para>
/// <para>
/// <b>Rolling back is the harness's job, not the author's.</b> A case that writes runs inside a
/// <see cref="TransactionGroup"/> which is always rolled back - in a <c>finally</c>, so it survives
/// the case throwing. Left to each author it would be forgotten exactly once, and the symptom is
/// not a failed test: it is Revit asking whether to save changes, at the end of an unattended run,
/// with nobody there to answer. This repository has already lost two sweeps to a modal dialog.
/// </para>
/// <para>
/// <b>The whole run is one trip to the API thread</b>, made by whoever calls this. Cases are read
/// from a model and compare what they read; splitting them across posts would let a document open
/// or close between two cases that were written about the same one.
/// </para>
/// </remarks>
public static class RevitTestRun
{
    /// <summary>Runs every case of every suite and returns their results, in declaration order.</summary>
    /// <param name="suites">The declared list. Nothing is scanned for.</param>
    /// <param name="application">The live session, from inside the pump.</param>
    public static IReadOnlyList<RevitTestResult> Execute(
        IEnumerable<IRevitTestSuite> suites,
        UIApplication application)
    {
        var results = new List<RevitTestResult>();

        foreach (var suite in suites ?? Array.Empty<IRevitTestSuite>())
        {
            if (suite is null)
                continue;

            // Enumerating the cases is the suite's own code and may throw. It is reported as one
            // failed case named after the suite rather than allowed to end the run, because a suite
            // that cannot list itself is a defect of the same size as a case that fails - and
            // ending the run here would take every later suite with it.
            List<RevitTestCase> cases;

            try
            {
                cases = new List<RevitTestCase>(suite.Cases ?? Array.Empty<RevitTestCase>());
            }
            catch (Exception error)
            {
                results.Add(new RevitTestResult(
                    suite.Name,
                    "the suite lists its cases",
                    RevitTestOutcome.Failed,
                    Describe(error),
                    TimeSpan.Zero,
                    Array.Empty<KeyValuePair<string, string>>()));
                continue;
            }

            foreach (var test in cases)
            {
                if (test is not null)
                    results.Add(Run(suite, test, application));
            }
        }

        return results;
    }

    private static RevitTestResult Run(IRevitTestSuite suite, RevitTestCase test, UIApplication application)
    {
        var notes = new List<KeyValuePair<string, string>>();
        var document = application?.ActiveUIDocument?.Document;
        var needsDocument = test.NeedsDocument || test.Writes;

        if (needsDocument && document is null)
        {
            return new RevitTestResult(
                suite.Name,
                test.Name,
                RevitTestOutcome.Skipped,
                "the case reads a model and this run has none open - sweep with --with-model",
                TimeSpan.Zero,
                notes);
        }

        var context = new RevitTestContext(application!, document, notes);
        var clock = Stopwatch.StartNew();

        try
        {
            if (test.Writes)
                InRolledBackGroup(document!, test, context);
            else
                test.Body(context);

            clock.Stop();
            return new RevitTestResult(suite.Name, test.Name, RevitTestOutcome.Passed, string.Empty, clock.Elapsed, notes);
        }
        catch (RevitTestFailure failure)
        {
            clock.Stop();
            return new RevitTestResult(suite.Name, test.Name, RevitTestOutcome.Failed, failure.Message, clock.Elapsed, notes);
        }
        catch (Exception error)
        {
            clock.Stop();

            // Named apart from an assertion because the two mean different things to whoever reads
            // the report: one says the code under test is wrong, the other says the test never got
            // far enough to have an opinion.
            return new RevitTestResult(
                suite.Name,
                test.Name,
                RevitTestOutcome.Failed,
                "threw before it could assert - " + Describe(error),
                clock.Elapsed,
                notes);
        }
    }

    /// <summary>Runs a writing case so that the document is as it was afterwards, whatever happens.</summary>
    /// <remarks>
    /// <c>RollBack</c> undoes transactions already committed inside the group, which is what makes
    /// this usable around a production write path rather than around a rehearsal of one - the same
    /// form the shared parameter scheme and the modal window measurement already use.
    /// </remarks>
    private static void InRolledBackGroup(Document document, RevitTestCase test, RevitTestContext context)
    {
        using var group = new TransactionGroup(document, "BHS test: " + test.Name);
        group.Start();

        try
        {
            test.Body(context);
        }
        finally
        {
            if (group.GetStatus() == TransactionStatus.Started)
                group.RollBack();
        }
    }

    private static string Describe(Exception error) =>
        error.GetType().Name + ": " + error.Message;

    /// <summary>
    /// The results as the flat string map the channel carries.
    /// </summary>
    /// <remarks>
    /// <b>The wire format lives here and is parsed by the runner</b>, which cannot reference this
    /// assembly: it is a .NET 10 console application and this is a Revit-TFM library. That is the
    /// same arrangement every other question the probe answers already has, and the keys are kept
    /// dull for exactly that reason - zero-padded indices, one fact per key, nothing that has to be
    /// split on a separator whose meaning could drift between the two ends.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Flatten(IReadOnlyList<RevitTestResult> results)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var prefix = "tests:" + index.ToString("D3", CultureInfo.InvariantCulture) + ":";

            map[prefix + "suite"] = result.Suite;
            map[prefix + "name"] = result.Name;
            map[prefix + "outcome"] = result.Outcome.ToString();
            map[prefix + "detail"] = result.Detail;
            map[prefix + "ms"] = result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);

            for (var note = 0; note < result.Notes.Count; note++)
            {
                map[prefix + "note:" + note.ToString("D2", CultureInfo.InvariantCulture)] =
                    result.Notes[note].Key + " = " + result.Notes[note].Value;
            }

            switch (result.Outcome)
            {
                case RevitTestOutcome.Passed: passed++; break;
                case RevitTestOutcome.Failed: failed++; break;
                default: skipped++; break;
            }
        }

        // Reported as well as derivable from the entries above, and on purpose: the runner checks
        // that the two agree. A results list that lost an entry on the way through the channel
        // would otherwise arrive looking like a smaller run that went perfectly.
        map["tests:reported"] = results.Count.ToString(CultureInfo.InvariantCulture);
        map["tests:passed"] = passed.ToString(CultureInfo.InvariantCulture);
        map["tests:failed"] = failed.ToString(CultureInfo.InvariantCulture);
        map["tests:skipped"] = skipped.ToString(CultureInfo.InvariantCulture);

        return map;
    }
}
