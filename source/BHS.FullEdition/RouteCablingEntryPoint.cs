using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.FullEdition;

/// <summary>
/// The class Revit constructs when the routing button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// Empty, in the edition's own assembly, and for the reasons set out on
/// <see cref="CollectCablingEntryPoint"/>: Revit resolves the class name inside the assembly the
/// button names, a generic name is refused by Revit 2024, and an empty subclass is what both
/// constraints leave. It is also what keeps the feature's assembly out of the AppDomain until
/// somebody presses this.
/// </para>
/// <para>
/// <b><c>Manual</c> even though this command writes nothing yet.</b> The alternative,
/// <c>ReadOnly</c>, would be truthful today and would have to change on the day the apply phase
/// lands - and that change is one line in a file nobody opens, guarded by a modal dialog nobody sees
/// until a customer does. The mode a command will need is not a thing to leave for later when
/// stating it now costs the same.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class RouteCablingEntryPoint : CommandEntryPoint<RouteCablingCommand>
{
}
