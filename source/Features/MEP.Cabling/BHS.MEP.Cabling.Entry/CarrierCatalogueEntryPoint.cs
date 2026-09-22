using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The class Revit constructs when the carrier catalogue is pressed.
/// </summary>
/// <remarks>
/// Empty, in the feature's Entry assembly, for the reasons set out on
/// <see cref="CollectCablingEntryPoint"/>. <c>Manual</c> because the command writes: the rules go
/// into the model's own settings layer, in one transaction it opens itself.
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class CarrierCatalogueEntryPoint : CommandEntryPoint<CablingFeature, CarrierCatalogueCommand>
{
}
