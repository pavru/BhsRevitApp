using Autodesk.Revit.Attributes;
using BHS.Revit.Abstractions;
using BHS.Revit.Probe.Declaration;
using BHS.Revit.Probe.Feature;

namespace BHS.Revit.Probe.Entry;

/// <summary>
/// The class Revit constructs when the probe's Entry button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// A feature's command entry point, reproduced in the probe so that the shape is measured before a
/// feature's buttons rest on it. Empty and sealed, for the reasons on <c>CommandEntryPoint</c>: Revit
/// resolves the name inside the assembly the button names, refuses a constructed generic name on 2024,
/// and reads <c>[Transaction]</c> off the type it constructs.
/// </para>
/// <para>
/// <b>Found by its feature.</b> The base asks the registry for the one host with a user interface whose
/// <c>Modules</c> list declared <see cref="ProbeGateFeature"/>; <see cref="GateCommand"/> writes down
/// which host it was handed and what <c>ActiveAddInId</c> said, and the sweep compares the two.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class GateCommandEntryPoint : CommandEntryPoint<ProbeGateFeature, GateCommand>
{
}
