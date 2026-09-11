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

    /// <summary>
    /// Half a metre - <b>an assumption, named as one</b>, and the owner's to correct.
    /// </summary>
    /// <remarks>
    /// The owner decided that one distance governs both merging taps and preferring an existing box;
    /// the value was not discussed. Half a metre keeps two sockets side by side in one box and keeps
    /// two sockets in different bays apart, which is the guess, and it is a setting because a guess
    /// about practice belongs where the project can overrule it.
    /// </remarks>
    public const double DefaultBoxRadiusMm = 500;

    private CablingProjectSettings(RecommendedBoxes boxes, CircuitConnection connection, double boxRadius, string unreadable)
    {
        Boxes = boxes;
        DefaultConnection = connection;
        BoxRadius = boxRadius;
        Unreadable = unreadable;
    }

    /// <summary>Which family stands for a recommended box.</summary>
    public RecommendedBoxes Boxes { get; }

    /// <summary>The project's answer for a circuit whose own parameter and panel's are both empty.</summary>
    public CircuitConnection DefaultConnection { get; }

    /// <summary>The box radius, in internal feet.</summary>
    public double BoxRadius { get; }

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

        return new CablingProjectSettings(RecommendedBoxes.Read(model), connection, radius, unreadable);
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
}
