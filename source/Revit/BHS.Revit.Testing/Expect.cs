using System.Globalization;

namespace BHS.Revit.Testing;

/// <summary>A case that failed, carrying the sentence the report will print.</summary>
/// <remarks>
/// Its own type rather than an assertion library, and the reason is the metric this repository
/// measures Revit-side assemblies by: how many assemblies somebody else might also ship. Every
/// assertion package brings its own, into an AppDomain Revit 2024 shares between every vendor -
/// and buys, for that, a vocabulary of comparisons over one sentence and a boolean.
/// </remarks>
public sealed class RevitTestFailure : Exception
{
    public RevitTestFailure(string message) : base(message)
    {
    }
}

/// <summary>A case that declined to assert anything, and said why.</summary>
/// <remarks>
/// Distinct from a failure because it means the opposite thing: not "the code is wrong" but "this
/// run could not put the question". See <see cref="Skip"/>.
/// </remarks>
public sealed class RevitTestSkipped : Exception
{
    public RevitTestSkipped(string message) : base(message)
    {
    }
}

/// <summary>Standing down, out loud.</summary>
/// <remarks>
/// <para>
/// <b>Added by the first run of this mechanism, which found the hole in its own tests.</b> The model
/// a sweep opens comes from <c>testdata</c> and is empty - deliberately, because the real models are
/// somebody's building and this repository is public. Four cases walked its carriers, found none,
/// and passed: every one of them asserts something of the form "for each carrier ...", and that is
/// true of no carriers at all. Green, honest, and about nothing.
/// </para>
/// <para>
/// <b>A vacuous pass is worse than a skip, and the difference is who can see it.</b> A skip is
/// printed by name with its reason and counts as nothing; a vacuous pass is indistinguishable from
/// a real one and inflates the very floor that is supposed to notice checks going missing.
/// </para>
/// <para>
/// <b>The obvious objection, and the answer.</b> A case that can skip itself is a case that can stop
/// asserting quietly - which is the failure being fixed, wearing a different hat. It is answered by
/// making the skip loud rather than by forbidding it: the reason is mandatory, the runner prints
/// case and reason on their own line, and a sweep in which everything skipped fails the check that
/// at least one case asserted something.
/// </para>
/// </remarks>
public static class Skip
{
    /// <param name="reason">
    /// What was missing, in terms of the run rather than of the code: "the model this sweep opened
    /// holds no carriers" tells a reader to look at the model, which is where the answer is.
    /// </param>
    public static void Because(string reason) => throw new RevitTestSkipped(reason);

    /// <summary>Stands down only when the condition holds, for the ordinary guard at the top of a case.</summary>
    public static void When(bool condition, string reason)
    {
        if (condition)
            throw new RevitTestSkipped(reason);
    }
}

/// <summary>The whole assertion vocabulary: say what should be true, and what it was instead.</summary>
public static class Expect
{
    /// <summary>Fails the case unless the condition holds.</summary>
    /// <param name="condition">What should be true.</param>
    /// <param name="failure">
    /// What to print when it is not - written as the finding, not as the expectation. "carrier 1187
    /// has no terminals" is read once; "expected terminals" sends the reader back to the source to
    /// find out which carrier and how many there were.
    /// </param>
    public static void That(bool condition, string failure)
    {
        if (!condition)
            throw new RevitTestFailure(failure);
    }

    /// <summary>Fails unless two counts agree, and says both when they do not.</summary>
    /// <remarks>
    /// Here because the mistake it prevents has already been made in this repository, twice, in
    /// prose: a comparison whose message names only one of the two numbers. The measurement of
    /// carrier approach summed two quantities over different sets and read as a saving; the icons
    /// measured a bounding box and read as a picture. Printing both sides costs one overload.
    /// </remarks>
    public static void Same(long expected, long actual, string what)
    {
        if (expected == actual)
            return;

        throw new RevitTestFailure(string.Format(
            CultureInfo.InvariantCulture,
            "{0}: expected {1}, got {2}",
            what,
            expected,
            actual));
    }
}
