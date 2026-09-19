using Autodesk.Revit.Attributes;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The class Revit constructs when the inspector's button is pressed: it shows the cabling inspector pane,
/// or hides it.
/// </summary>
/// <remarks>
/// Empty and sealed like every Entry class; the base finds the pane by this class's own name, which the host
/// filed from the manifest at startup. <c>ReadOnly</c>, because showing a pane touches no document.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public sealed class CablingInspectorEntryPoint : PaneEntryPoint
{
}
