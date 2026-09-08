using Autodesk.Revit.Attributes;
using BHS.MEP.Cabling.Feature;
using BHS.Revit.Abstractions;

namespace BHS.FullEdition;

/// <summary>
/// The class Revit constructs when the button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty, in the edition's own assembly, and both halves of that are measured.</b> Revit resolves
/// the class name from the manifest inside the assembly the button names, so a type living
/// elsewhere is never found - and a generic name such as <c>CommandEntryPoint`1[FooCommand]</c> is
/// refused outright by Revit 2024, whatever spelling it is given. An empty subclass is what both
/// constraints leave.
/// </para>
/// <para>
/// <b>It is also what keeps the feature unloaded.</b> The CLR resolves a base type when it first
/// constructs the derived one, so <c>BHS.MEP.Cabling.Feature.dll</c> stays out of the AppDomain
/// while the ribbon merely exists - which on Revit 2024 matters, because every add-in shares one
/// AppDomain and a loaded assembly holds its simple name for the session. Measured in the probe on
/// all four releases, with an assembly that exists for exactly this question.
/// </para>
/// <para>
/// <b><c>[Transaction]</c> goes here rather than on the base, and that is deliberate.</b> Revit
/// reads it from the type it created; the attribute is inheritable in principle, and whether Revit
/// asks with <c>inherit: true</c> has not been measured. It stays here anyway, because the mode is
/// a property of a command: an inherited one is a mode nobody chose. <c>RVTRIB003</c> requires it
/// directly, knowingly stricter than the CLR.
/// </para>
/// <para>
/// <c>Manual</c> because collecting is reading. A command that opens no transaction should not be
/// handed one already open - <c>ReadOnly</c> would say the same about intent but forbids the write
/// this command will grow when the apply phase lands.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class CollectCablingEntryPoint : CommandEntryPoint<CollectCablingCommand>
{
}
