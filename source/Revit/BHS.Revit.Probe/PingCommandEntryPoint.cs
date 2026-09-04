using Autodesk.Revit.Attributes;
using BHS.Revit.Abstractions;
using BHS.Revit.Probe.Feature;

namespace BHS.Revit.Probe;

/// <summary>
/// The whole of what a generator would emit for one command.
/// </summary>
/// <remarks>
/// One line, and every part of it is load-bearing:
/// <list type="bullet">
/// <item><b>not generic</b>, so the name Revit is handed is a plain one - a constructed generic name
/// given as text is accepted on 2026 and refused on 2024, with a dialog, measured both ways;</item>
/// <item><b>in the edition assembly</b>, which is where Revit insists an availability class lives -
/// also measured, also a dialog when it is not;</item>
/// <item><b>derived rather than written</b>, so the logic lives once in the base and a generator
/// emits empty classes instead of dispatcher bodies.</item>
/// </list>
/// The predecessor reached this shape for its application entry point and named a plain class in the
/// manifest for exactly the same reason. This applies it to commands, which is where it saves the
/// work.
/// <para>
/// It is also what makes the feature assembly load late: the CLR resolves a type when it is first
/// needed, so the base type here - and with it <c>BHS.Revit.Probe.Feature</c> - is resolved when
/// Revit constructs this class, at the press, and not while the ribbon is being built.
/// </para>
/// <para>
/// <b>The transaction mode lives here, and it has to.</b> Revit reads <c>[Transaction]</c> off the
/// type it constructs, which is this one - measured, and the failure is a dialog saying the add-in
/// has no Transaction attribute. So the attribute on the feature's own command would mean nothing,
/// and the mode is a property of each command rather than of the base. That is why it belongs in the
/// manifest a generator reads, with no default: a command whose mode nobody stated is a button that
/// fails when it is pressed.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public sealed class PingCommandEntryPoint : CommandEntryPoint<PingCommand>
{
}
