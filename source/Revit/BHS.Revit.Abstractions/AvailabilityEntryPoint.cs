using System.Globalization;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Abstractions;

/// <summary>What a feature writes instead of an <c>IExternalCommandAvailability</c>.</summary>
/// <remarks>
/// <para>
/// A rule answers one question - may this command be offered right now - from what the context
/// carries and nothing else. It lives in the feature's declaration assembly, which is loaded at
/// startup anyway, so asking it loads nothing a press would not have loaded.
/// </para>
/// <para>
/// <b>Asked often, and by something that must not wait.</b> Revit calls availability "any time there
/// is a contextual change" and requires the callback to be fast and non-blocking (RevitAPIUI.xml). A
/// rule that reads a collector, awaits the pump or opens a transaction breaks that on the thread
/// Revit's own interface runs on.
/// </para>
/// </remarks>
public interface IAvailabilityRule
{
    bool IsAvailable(AvailabilityContext context);
}

/// <summary>Everything a rule is allowed to look at.</summary>
/// <remarks>
/// <para>
/// <b>Two members, and the missing ones are the design.</b> No <see cref="IFeatureServices"/>, no
/// <c>UIApplication</c>, no settings, no pump. Read from the IL of all four supported releases: Revit
/// keeps <b>one</b> availability instance per assembly path and class name in a static
/// <c>Hashtable</c> for the whole session, never removes it, and shares it between every button
/// naming that class. Anything a rule could keep would therefore go stale, and anything reachable
/// through services is one call away from breaking "fast and non-blocking": model settings run a
/// collector on first read, and awaiting the pump from the API thread deadlocks.
/// </para>
/// <para>
/// A struct so that building one on every call costs no allocation; readonly because a rule has no
/// business changing what it was shown.
/// </para>
/// </remarks>
public readonly struct AvailabilityContext
{
    public AvailabilityContext(Document? document, CategorySet selectedCategories)
    {
        Document = document;
        SelectedCategories = selectedCategories;
    }

    /// <summary>The active document, or null when there is none.</summary>
    public Document? Document { get; }

    /// <summary>
    /// The categories of the current selection. Documented as an empty set rather than null when
    /// nothing is selected or no document is open; not measured.
    /// </summary>
    public CategorySet SelectedCategories { get; }
}

/// <summary>
/// The bridge between the availability class name Revit resolves and the rule a feature wrote.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="CommandEntryPoint{TFeature, TCommand}"/>, and for the same measured
/// reasons: Revit resolves <c>AvailabilityClassName</c> inside the one assembly the button names, so
/// the class it constructs has to be declared there, and a constructed generic name is refused by
/// Revit 2024 - so an empty derived class carries a plain name and this base carries the logic.
/// </para>
/// <code>
/// public sealed class NeedsProjectDocumentEntryPoint : AvailabilityEntryPoint&lt;NeedsProjectDocument&gt; { }
/// </code>
/// <para>
/// <b>A new rule on every call, so a rule is stateless by construction.</b> Revit holds this instance
/// for the whole session and shares it between buttons (see <see cref="AvailabilityContext"/>);
/// building the rule afresh is what makes "the rule kept something" impossible rather than merely
/// discouraged.
/// </para>
/// <para>
/// <b>No host lookup, and <c>ActiveAddInId</c> is never read.</b> A rule needs no services, so there
/// is nothing to look up - and a lookup here would be a dictionary walk on every contextual change,
/// for an answer nobody uses.
/// </para>
/// <para>
/// <b>A rule that throws makes the button available, not grey.</b> Owner decision: the button stays
/// enabled and the command, which can speak, explains the refusal. The alternative is worse than it
/// looks. Read from the IL: an exception escaping this method is caught by Revit itself, answered as
/// <c>false</c> - so the button greys out for a reason nobody can see - and the same throw is asked
/// for again at every contextual change after that.
/// </para>
/// </remarks>
public abstract class AvailabilityEntryPoint<TRule> : IExternalCommandAvailability
    where TRule : IAvailabilityRule, new()
{
    /// <summary>Whether this rule's first failure has been written down. One per closed type.</summary>
    /// <remarks>
    /// The one named exception to "no state". It cannot go stale, because it records something about
    /// the rule's code rather than about Revit, and it has to exist: a rule that throws throws at every
    /// contextual change, and a line per change would bury the log the line is meant to be found in.
    /// </remarks>
    private static int _reported;

    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        try
        {
            var document = applicationData?.ActiveUIDocument?.Document;

            return new TRule().IsAvailable(new AvailabilityContext(document, selectedCategories));
        }
        catch (Exception error)
        {
            Report(error);
            return true;
        }
    }

    /// <summary>Writes the first failure of this rule, below Warning, and never throws.</summary>
    /// <remarks>
    /// Below Warning on purpose. This runs on the API thread, where Warning and above also go to the
    /// Revit journal - and what the journal sink does inside an availability callback, which runs
    /// inside Revit's own interface update, has not been measured. The log file gets the line either
    /// way, with the exception.
    /// </remarks>
    private void Report(Exception error)
    {
        if (Interlocked.CompareExchange(ref _reported, 1, 0) != 0)
            return;

        try
        {
            var log = Log.For(EntryPoints.LogCategory);

            if (!log.IsEnabled(LogLevel.Information))
                return;

            log.Write(LogLevel.Information,
                string.Format(CultureInfo.InvariantCulture,
                    "availability rule {0} threw inside {1}, so the button stays available and the command has to explain itself; reported once per rule",
                    typeof(TRule).FullName, GetType().FullName),
                error);
        }
        catch (Exception)
        {
            // Reporting a failure must never become a second one inside Revit's interface update.
        }
    }
}
