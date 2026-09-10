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
