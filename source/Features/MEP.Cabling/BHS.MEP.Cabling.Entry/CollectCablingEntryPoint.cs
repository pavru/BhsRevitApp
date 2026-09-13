using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The class Revit constructs when the read-structure button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty, in the feature's Entry assembly, and both halves of that are measured.</b> Revit resolves
/// the class name from the manifest inside the assembly the button names, so a type living elsewhere
/// is never found - and a generic name such as <c>CommandEntryPoint`2[...]</c> is refused outright by
/// Revit 2024, whatever spelling it is given. An empty subclass is what both constraints leave. It
/// used to sit in the edition; it sits here so that every edition offering cabling gets it by
/// referencing this project instead of writing it again.
/// </para>
/// <para>
/// <b>It is also what keeps the feature unloaded.</b> The CLR resolves a base type when it first
/// constructs the derived one, so <c>BHS.MEP.Cabling.Feature.dll</c> stays out of the AppDomain
/// while the ribbon merely exists - which on Revit 2024 matters, because every add-in shares one
/// AppDomain and a loaded assembly holds its simple name for the session. Measured in the probe on
/// all four releases, for an entry point in the edition's own assembly. With the entry point here,
/// this assembly is loaded earlier, when the availability class beside it is constructed; that the
/// feature still stays out until a press is expected and not measured.
/// </para>
/// <para>
/// <b>The host is found by <see cref="CablingFeature"/>, not by this assembly.</b> Every edition that
/// offers cabling references this one, so where the entry point sits says nothing about which edition
/// is running it; the edition's <c>Modules</c> list does, and the base asks the registry for the host
/// that declared the feature.
/// </para>
/// <para>
/// <b><c>[Transaction]</c> goes here rather than on the base, and that is deliberate.</b> Revit
/// reads it from the type it created; the attribute is inheritable in principle, and whether Revit
/// asks with <c>inherit: true</c> has not been measured. It stays here anyway, because the mode is
/// a property of a command: an inherited one is a mode nobody chose. <c>RVTRIB003</c> requires it
/// directly, knowingly stricter than the CLR.
/// </para>
/// <para>
/// <c>Manual</c> because collecting is reading, and a command that opens no transaction should not be
/// handed one already open. <c>ReadOnly</c> would say the same about intent; it was passed over for
/// the write this command was expected to grow when the apply phase landed, and the apply phase
/// landed in the routing command instead. The mode is carried over as it was rather than changed in a
/// move: a transaction mode is changed on purpose, with its own reason, and not as a side effect of a
/// file changing assembly.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class CollectCablingEntryPoint : CommandEntryPoint<CablingFeature, CollectCablingCommand>
{
}
