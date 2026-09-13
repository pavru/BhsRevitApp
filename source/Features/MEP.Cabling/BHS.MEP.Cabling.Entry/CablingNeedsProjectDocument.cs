using BHS.MEP.Cabling.Declaration;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Entry;

/// <summary>
/// The availability class the cabling buttons name. The rule itself is
/// <see cref="NeedsProjectDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the same assembly as the command entry points because Revit gives it no choice.</b> The class
/// name on a button is resolved inside the assembly the button names, so an availability class
/// anywhere else is never found - and the failure is a modal <c>TypeLoadException</c> in front of the
/// user, not a greyed-out button. Measured, and read again from the IL of all four releases, which is
/// why the rule is written in the declaration and only this name for it lives here.
/// </para>
/// <para>
/// <b>Empty, and the emptiness is load-bearing.</b> Revit keeps one instance of this class per
/// assembly path and class name for the whole session and shares it between all three buttons; the
/// base builds a fresh rule on every call, so there is nothing here that could go stale between them.
/// </para>
/// </remarks>
public sealed class CablingNeedsProjectDocument : AvailabilityEntryPoint<NeedsProjectDocument>
{
}
