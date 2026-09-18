using Autodesk.Revit.Attributes;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe.Entry;

/// <summary>
/// The class Revit constructs when the probe pane's button is pressed: it shows the pane, or hides it.
/// </summary>
/// <remarks>
/// A feature's pane button, reproduced in the probe before a feature's pane rests on it. Empty and
/// sealed like every Entry class; the base finds the pane by this class's own name, which the host
/// filed from the manifest at startup. <c>ReadOnly</c>, because showing a pane touches no document.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ProbePaneEntryPoint : PaneEntryPoint
{
}
