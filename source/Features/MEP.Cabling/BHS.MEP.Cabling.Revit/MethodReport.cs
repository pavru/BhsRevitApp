using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// The length a run laid by methods the project gave no slot, said in one line for the screen and the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's fourth and fifth answers of 2026-09-22</b>: a method with no slot - a seventh, or a
/// typo - and a carrier whose type names no method both put their length into «прочая», and are named
/// here. The model cannot name them: a column per unexpected method is the registry the six slots
/// exist to avoid.
/// </para>
/// <para>
/// <b>Silent when the methods are off, and when everything the run walked has a slot.</b> The rule
/// this window follows for cable groups, box capacity and the catalogue: a line that is true after
/// every run buries the lines that are findings.
/// </para>
/// </remarks>
public static class MethodReport
{
    /// <param name="run">The finished run.</param>
    /// <param name="methods">The project's slots.</param>
    /// <param name="length">How the window writes a length - Revit's own formatting of the document's units.</param>
    public static string Describe(RouteRun run, InstallationMethods methods, Func<double, string> length)
    {
        if (run is null || methods is null || !methods.IsOn)
            return string.Empty;

        // By method, in the order first met, with how many circuits laid any of it: the count is what
        // says whether this is one stray type or the project's whole structure.
        var order = new List<string>();
        var lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var circuits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in run.Results)
        {
            if (route.Status != RouteStatus.Found)
                continue;

            foreach (var part in route.AlongByMethod)
            {
                if (part.Value <= 0 || methods.SlotOf(part.Key) != 0)
                    continue;

                if (!lengths.ContainsKey(part.Key))
                {
                    order.Add(part.Key);
                    lengths[part.Key] = 0;
                    circuits[part.Key] = 0;
                }

                lengths[part.Key] += part.Value;
                circuits[part.Key]++;
            }
        }

        if (order.Count == 0)
            return string.Empty;

        var named = order.Select(one =>
            (one.Length == 0 ? "carriers whose type names no method" : "'" + one + "'")
            + " " + length(lengths[one]) + " on " + circuits[one] + " circuit(s)");

        return "Laid by no method this project gave a slot, so counted in the other length (BHS_Cbl_LengthOther): "
            + string.Join("; ", named)
            + ". Name the method in one of the six slots, or fill in '" + methods.Parameter + "' on the carrier types.";
    }
}
