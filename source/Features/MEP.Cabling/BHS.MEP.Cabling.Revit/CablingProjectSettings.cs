using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Settings;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// What a project says about how its cables are laid, read once per run on the API thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Project-scoped, hence <c>Model:</c> on every key.</b> How a project wires its devices and how
/// far apart two taps may be and still share a box are rules of the project, not preferences of the
/// person running the tool. The chain is product, machine, the model, the environment; the user's
/// own file has no say, which is what a project rule means.
/// </para>
/// <para>
/// One object rather than three readers, because the three are read at the same moment for the same
/// run, and a command that read two of them and forgot the third would compute boxes for one
/// project with another project's radius.
/// </para>
/// </remarks>
public sealed class CablingProjectSettings
{
    /// <summary>How a circuit is connected when neither it nor its panel says.</summary>
    public const string ConnectionKey = "Model:Cabling:Connection";

    /// <summary>The box radius, in millimetres.</summary>
    public const string BoxRadiusKey = "Model:Cabling:BoxRadiusMm";

    /// <summary>Whether circuits cut in boxes are served only from boxes already in the model.</summary>
    /// <remarks>
    /// The owner's decision: a project rule, here with its neighbours rather than a parameter on the
    /// circuit, because it is how the project is built and not a property of one circuit. Changed from
    /// the routing window and written back when a run is applied.
    /// </remarks>
    public const string ExistingBoxesOnlyKey = "Model:Cabling:ExistingBoxesOnly";

    /// <summary>A hundred and fifty millimetres - the owner's value, 2026-09-13.</summary>
    /// <remarks>
    /// <b>It replaces a guess of mine, and the difference is the point.</b> Half a metre was written
    /// here as an assumption, flagged as one, because the owner had decided that one distance governs
    /// both merging taps and preferring an existing box but had not named it. A hundred and fifty
    /// millimetres is about the size of a box, so what shares one is what would physically fit in
    /// one - which is a rule about the thing rather than about how near two sockets look on a plan.
    /// </remarks>
    public const double DefaultBoxRadiusMm = 150;

    private CablingProjectSettings(
        RecommendedBoxes boxes,
        CircuitConnection connection,
        double boxRadius,
        bool existingBoxesOnly,
        string unreadable)
    {
        Boxes = boxes;
        DefaultConnection = connection;
        BoxRadius = boxRadius;
        ExistingBoxesOnly = existingBoxesOnly;
        Unreadable = unreadable;
    }

    /// <summary>Which family stands for a recommended box.</summary>
    public RecommendedBoxes Boxes { get; }

    /// <summary>The project's answer for a circuit whose own parameter and panel's are both empty.</summary>
    public CircuitConnection DefaultConnection { get; }

    /// <summary>The box radius, in internal feet.</summary>
    public double BoxRadius { get; }

    /// <summary>Whether circuits cut in boxes are served only from boxes already in the model.</summary>
    public bool ExistingBoxesOnly { get; }

    /// <summary>A setting that is present and cannot be read, or empty.</summary>
    /// <remarks>
    /// Reported rather than swallowed, the rule this repository applies to every value: absence is
    /// ordinary and gets the default, a typo is not and gets said out loud.
    /// </remarks>
    public string Unreadable { get; }

    /// <summary>Reads the project's answers. Call on the API thread: the radius is converted by Revit.</summary>
    public static CablingProjectSettings Read(ISettings model)
    {
        var text = model.Text(ConnectionKey, null);
        var unreadable = string.Empty;
        var connection = CircuitConnection.AtTerminal;

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (CircuitConnections.TryParse(text!, out var parsed))
                connection = parsed;
            else
                unreadable = ConnectionKey + " = '" + text + "'";
        }

        var radius = UnitUtils.ConvertToInternalUnits(
            model.Real(BoxRadiusKey, DefaultBoxRadiusMm),
            UnitTypeId.Millimeters);

        // Soft, like the connection above and unlike the reader's default: a mode nobody can read runs
        // the ordinary mode and is named, rather than stopping the command over one project value.
        var existingBoxesOnly = false;

        try
        {
            existingBoxesOnly = model.Flag(ExistingBoxesOnlyKey, false);
        }
        catch (InvalidOperationException)
        {
            var said = ExistingBoxesOnlyKey + " = '" + model[ExistingBoxesOnlyKey] + "'";
            unreadable = unreadable.Length == 0 ? said : unreadable + "; " + said;
        }

        return new CablingProjectSettings(RecommendedBoxes.Read(model), connection, radius, existingBoxesOnly, unreadable);
    }
}

/// <summary>The values <c>BHS_Cbl_CircuitConnection</c> and the project setting may hold.</summary>
/// <remarks>
/// <b>Values are not translated</b> - the decision recorded with the parameter scheme. A name is read
/// by a person and a value is compared by code; translating <c>JunctionBox</c> would make one family
/// mean different things depending on which Revit its author had open.
/// </remarks>
public static class CircuitConnections
{
    public static bool TryParse(string text, out CircuitConnection connection)
    {
        var value = (text ?? string.Empty).Trim();

        if (string.Equals(value, CablingParameters.ConnectionAtJunctionBox, StringComparison.OrdinalIgnoreCase))
        {
            connection = CircuitConnection.AtJunctionBox;
            return true;
        }

        if (string.Equals(value, CablingParameters.ConnectionAtTerminal, StringComparison.OrdinalIgnoreCase))
        {
            connection = CircuitConnection.AtTerminal;
            return true;
        }

        connection = CircuitConnection.AtTerminal;
        return false;
    }

    /// <summary>The value a resolved connection is written back as.</summary>
    /// <remarks>
    /// <b>Beside <see cref="TryParse"/> on purpose.</b> Reading and writing are one rule about one
    /// vocabulary, and the day they live in two files is the day a run writes a word its own reader
    /// does not recognise. Not translated, for the reason recorded on
    /// <c>CablingParameters.JunctionBoxRole</c>: the name is read by a person, the value is compared
    /// by code.
    /// </remarks>
    public static string Text(CircuitConnection connection) =>
        connection == CircuitConnection.AtJunctionBox
            ? CablingParameters.ConnectionAtJunctionBox
            : CablingParameters.ConnectionAtTerminal;
}
