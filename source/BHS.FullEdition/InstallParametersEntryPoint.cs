using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.FullEdition;

/// <summary>
/// The class Revit constructs when the shared-parameter button is pressed.
/// </summary>
/// <remarks>
/// Empty and in the edition's own assembly, for the reasons on <see cref="CollectCablingEntryPoint"/>.
/// <c>Manual</c> because this one really does write: it binds parameters to categories, which is a
/// modification, and it opens the transaction itself.
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class InstallParametersEntryPoint : CommandEntryPoint<InstallParametersCommand>
{
}
