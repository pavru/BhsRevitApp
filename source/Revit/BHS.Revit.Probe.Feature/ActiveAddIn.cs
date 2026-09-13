using Autodesk.Revit.UI;

namespace BHS.Revit.Probe.Feature;

/// <summary>What Revit says is executing, written down as it was said.</summary>
/// <remarks>
/// <para>
/// <b>Raw, and that is the point.</b> The host decides which add-in a command belongs to, and a command
/// that only reported the host it was handed would report the decision rather than the evidence.
/// <c>ActiveAddInId</c> is the evidence, and until this was written nobody had recorded it: the
/// earlier "measured" was the host id, which the assembly fallback could have produced as well. First
/// recorded 2026-09-14 - it named the pressed add-in on all four releases - and it stays recorded beside
/// the host, not instead of it.
/// </para>
/// <para>
/// A null is written as <c>(null)</c> and a throw as the exception's type, because a missing answer
/// and a refused one are different findings and neither may read as an id.
/// </para>
/// </remarks>
internal static class ActiveAddIn
{
    public static string Describe(ExternalCommandData? data)
    {
        try
        {
            var id = data?.Application?.ActiveAddInId;
            return id is null ? "(null)" : id.GetGUID().ToString();
        }
        catch (Exception error)
        {
            return "(threw " + error.GetType().Name + ")";
        }
    }
}
