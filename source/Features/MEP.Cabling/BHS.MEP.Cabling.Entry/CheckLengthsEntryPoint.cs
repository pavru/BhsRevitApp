using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The class Revit constructs when the length check is pressed.
/// </summary>
/// <remarks>
/// <para>
/// Empty, in the feature's Entry assembly, for the reasons set out on
/// <see cref="CollectCablingEntryPoint"/>.
/// </para>
/// <para>
/// <b><c>Manual</c> although this command writes nothing, and that is deliberate.</b> The mode is a
/// property of the command and has no default - a command whose mode nobody named is a button that
/// throws when pressed - and <c>ReadOnly</c> would be truthful only for as long as the command stays
/// read-only. The routing entry point already learned that lesson the expensive way: it said
/// <c>ReadOnly</c> until the apply phase landed, and the correction was one line in a file nobody
/// opens, guarded by a modal dialog nobody sees until a customer does.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class CheckLengthsEntryPoint : CommandEntryPoint<CablingFeature, CheckLengthsCommand>
{
}
