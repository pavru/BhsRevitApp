using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The class Revit constructs when the routing button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// Empty, in the feature's Entry assembly, and for the reasons set out on
/// <see cref="CollectCablingEntryPoint"/>: Revit resolves the class name inside the assembly the
/// button names, a generic name is refused by Revit 2024, and an empty subclass is what both
/// constraints leave. It is also what keeps the feature's assembly out of the AppDomain until
/// somebody presses this.
/// </para>
/// <para>
/// <b><c>Manual</c>, and the apply phase has since said why.</b> When this was written the command
/// wrote nothing, and <c>ReadOnly</c> would have been truthful - and would have had to change on the
/// day the apply phase landed, as one line in a file nobody opens, guarded by a modal dialog nobody
/// sees until a customer does. That day came: the routing window now writes the run into the model
/// from the same continuation, inside a transaction the feature opens itself. The mode a command will
/// need was not a thing to leave for later when stating it early cost the same.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class RouteCablingEntryPoint : CommandEntryPoint<CablingFeature, RouteCablingCommand>
{
}
