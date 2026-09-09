using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Settings;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// The routing options, as a person writes them and as the search needs them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Millimetres in the file, internal feet in the search, and the conversion happens exactly
/// here.</b> Nobody writing a settings file thinks in Revit's internal units, and nothing in
/// <see cref="RoutingOptions"/> is allowed to know what a millimetre is - so the boundary between
/// the two is a single method with a document in hand, which is the only place that can ask Revit
/// how to convert.
/// </para>
/// <para>
/// The owner's own words for the first of these: "treat elements as joined when they are no further
/// apart than this". It is a user setting rather than a constant because models differ - trays are
/// routinely drawn butted together with no connection between runs, and how large a gap still reads
/// as one route is a judgement about a particular project.
/// </para>
/// </remarks>
internal static class CablingOptions
{
    /// <summary>How large a gap still counts as a joint, in millimetres.</summary>
    private const double DefaultJoinToleranceMm = 50;

    /// <summary>How far a device may be from the structure, in millimetres.</summary>
    /// <remarks>
    /// Three metres, because a socket is on a wall and the tray is above the ceiling. Too small and
    /// every circuit reports "no carrier near"; too large and a device is attached to a run in the
    /// next room. It is the setting most likely to be changed per project.
    /// </remarks>
    private const double DefaultMaxApproachMm = 3000;

    /// <summary>
    /// The join tolerances a failed run is measured against, in millimetres.
    /// </summary>
    /// <remarks>
    /// Spread rather than stepped: the question is which order of magnitude closes the gaps, and
    /// five values across a factor of forty answer it where twenty values a millimetre apart would
    /// only make a longer list. A metre is the top because a tolerance that wide joins runs in
    /// different rooms, and past that the table stops being about joints.
    /// </remarks>
    public static IReadOnlyList<double> ToleranceLadder { get; } = new[] { 50.0, 100, 250, 500, 1000 };

    public static RoutingOptions Read(ISettings settings)
    {
        var join = settings.Real("Cabling:JoinToleranceMm", DefaultJoinToleranceMm);
        var approach = settings.Real("Cabling:MaxApproachMm", DefaultMaxApproachMm);
        var extend = settings.Real("Cabling:LengthExtend", 0);
        var preferConduit = settings.Real("Cabling:PreferConduitUntil", 0);

        return new RoutingOptions
        {
            JoinTolerance = ToFeet(join),
            MaxApproach = ToFeet(approach),

            // Not a distance: a fraction of the computed length, and a ratio between two lengths.
            // Converting either of them would be the kind of unit error that produces a plausible
            // number nobody can trace.
            LengthExtend = extend,
            PreferConduitUntil = preferConduit,

            AxisAlignedApproach = settings.Flag("Cabling:AxisAlignedApproach", true),
            SkipSingleDeviceCircuits = settings.Flag("Cabling:SkipSingleDeviceCircuits", false),
        };
    }

    /// <summary>
    /// Millimetres to Revit's internal units, asked of Revit rather than multiplied by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UnitUtils.ConvertToInternalUnits(Double, ForgeTypeId)</c> and
    /// <c>UnitTypeId.Millimeters</c> are present on all four supported releases - checked against
    /// the metadata rather than remembered, because the unit API changed shape in 2021.
    /// </para>
    /// <para>
    /// <b>No document, and that is not an oversight.</b> Millimetres to feet is a fixed ratio; the
    /// document's own unit settings decide how a number is <i>shown</i> and how one typed by a
    /// person is read, neither of which is happening here. A parameter taken and discarded would
    /// suggest this conversion depends on the model, and the next person would keep it.
    /// </para>
    /// </remarks>
    public static double ToFeet(double millimetres) =>
        UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);
}
