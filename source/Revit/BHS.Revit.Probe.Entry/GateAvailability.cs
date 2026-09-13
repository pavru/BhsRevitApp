using BHS.Revit.Abstractions;
using BHS.Revit.Probe.Declaration;

namespace BHS.Revit.Probe.Entry;

/// <summary>
/// The availability class the probe's Entry button names. The rule, and the counters the sweep reads,
/// are <see cref="GateRule"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first availability class whose logic lives in another assembly.</b> Revit constructs it
/// from a name inside this assembly, and everything it does - building the rule, catching what the rule
/// throws - is in <c>AvailabilityEntryPoint</c> in <c>BHS.Revit.Abstractions</c>. RefCheck accepts
/// that shape against the metadata; whether Revit accepts it is the question the sweep asks.
/// </para>
/// <para>
/// <b>Empty, even though it is a probe.</b> Revit keeps one instance per assembly path and class name
/// for the session, and anything counted here would be counted in the one place the owner ruled out.
/// </para>
/// </remarks>
public sealed class GateAvailability : AvailabilityEntryPoint<GateRule>
{
}
