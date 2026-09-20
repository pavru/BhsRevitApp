using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Which categories carry cable, and what class each one counts as.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list rather than a switch, because the predecessor's switch grew.</b> It hard-coded trays
/// and conduits, then acquired nine device categories and a generic-model escape hatch for the
/// models where somebody had drawn a tray as something else. The set is the user's to configure;
/// the defaults below are only what a project usually looks like.
/// </para>
/// <para>
/// The class is a string for the same reason it is a string on
/// <see cref="BHS.MEP.Cabling.Routing.CarrierNode.Class"/>: the preference between conduit and tray
/// is expressed in it, and a user who adds a category has to be able to say which of the two it
/// behaves like - or to name a third.
/// </para>
/// </remarks>
public sealed class CarrierCatalogue
{
    /// <summary>The class name the routing options' conduit preference is expressed against.</summary>
    public const string Conduit = "conduit";

    /// <summary>The class name for tray-like carriers.</summary>
    public const string Tray = "tray";

    private readonly Dictionary<BuiltInCategory, string> _classes;

    /// <summary>Which classes a cable may leave anywhere along, by class name.</summary>
    /// <remarks>
    /// A class not named here is open, and that is the shipped answer rather than an oversight: a
    /// pipe is the exception among carriers, and a project that invents a class - trunking, say -
    /// means something a cable comes out of.
    /// </remarks>
    private readonly Dictionary<string, bool> _open;

    public CarrierCatalogue(
        IReadOnlyDictionary<BuiltInCategory, string>? classes = null,
        IReadOnlyDictionary<string, bool>? openAlongTheirLength = null)
    {
        _open = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [Tray] = true,
            [Conduit] = false,
        };

        if (openAlongTheirLength is not null)
        {
            foreach (var pair in openAlongTheirLength)
                _open[pair.Key] = pair.Value;
        }

        if (classes is null)
        {
            _classes = new Dictionary<BuiltInCategory, string>
            {
                [BuiltInCategory.OST_CableTray] = Tray,
                [BuiltInCategory.OST_CableTrayFitting] = Tray,
                [BuiltInCategory.OST_Conduit] = Conduit,
                [BuiltInCategory.OST_ConduitFitting] = Conduit,
            };

            return;
        }

        // Copied pair by pair rather than cast to IDictionary. The cast works for a Dictionary and
        // throws for every other implementation of the read-only interface the parameter asks for -
        // an exception thrown by the type that accepted the argument, on a caller that did nothing
        // wrong.
        _classes = new Dictionary<BuiltInCategory, string>(classes.Count);

        foreach (var pair in classes)
            _classes[pair.Key] = pair.Value;
    }

    /// <summary>The categories to collect, host and link alike.</summary>
    public IEnumerable<BuiltInCategory> Categories => _classes.Keys;

    /// <summary>What class a category counts as, or empty when it is not a carrier at all.</summary>
    public string ClassOf(BuiltInCategory category) =>
        _classes.TryGetValue(category, out var found) ? found : string.Empty;

    /// <summary>Whether a cable may leave carriers of this class anywhere along them.</summary>
    /// <remarks>
    /// <para>
    /// <b>Moved here from the routing core on 2026-09-20, and the move is the point.</b> The core
    /// used to answer it itself, as <c>Class != "conduit"</c> - a rule about how carriers behave,
    /// written as a string comparison, in the one assembly the user cannot configure. The class is
    /// what a project configures, so a project naming a third class had no way to say what it
    /// behaves like; now it says so here, beside the categories, and the core is told the answer.
    /// </para>
    /// <para>
    /// This is not the same question as whether cable may be <i>spliced</i> in a carrier. A tray is
    /// open along its length and is still no place for a splice; a trunking with a removable cover is
    /// both. The first is a property of the class and lives here; the second is a property of the
    /// Revit type and lives on the element - see <c>CablingParameters.Splicing</c>.
    /// </para>
    /// </remarks>
    public bool IsOpenAlongItsLength(string carrierClass) =>
        !_open.TryGetValue(carrierClass ?? string.Empty, out var open) || open;
}
