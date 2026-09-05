using System.Text.Json;

namespace BimHouse.RefCheck;

/// <summary>One finding about one button.</summary>
internal readonly record struct RibbonFinding(string Code, string Manifest, string Message);

/// <summary>
/// Checks a ribbon manifest against the assembly it names.
/// </summary>
/// <remarks>
/// <para>
/// The ribbon is built from strings so that the assemblies behind the buttons stay unloaded until one
/// is pressed - measured, and the reason the manifest exists at all. The price is that every mistake
/// a compiler would have caught becomes a modal dialog in front of a person instead. These checks buy
/// that back, and each one is a dialog this project actually hit:
/// </para>
/// <list type="bullet">
/// <item><description><b>RVTRIB001</b> - the class is not in the assembly. Revit says the class was not
/// found in the add-in assembly and refuses.</description></item>
/// <item><description><b>RVTRIB002</b> - the class is not an <c>IExternalCommand</c>.</description></item>
/// <item><description><b>RVTRIB003</b> - no <c>[Transaction]</c> on the class Revit constructs. The
/// dialog arrives when the button is pressed, which may be months later.</description></item>
/// <item><description><b>RVTRIB004</b> - the availability class is in another assembly. Revit takes the
/// assembly name from the button and resolves the class inside it and nowhere else, so it never
/// finds it, and throws <c>TypeLoadException</c> at the user.</description></item>
/// <item><description><b>RVTRIB005</b> - the availability class is not an
/// <c>IExternalCommandAvailability</c>.</description></item>
/// </list>
/// <para>
/// It lives here rather than in the MSBuild task that writes the manifest, and the reason is this
/// repository's oldest lesson: reading metadata needs <c>System.Reflection.Metadata</c>, which on
/// .NET Framework arrives with five polyfills - <c>System.Memory</c> among them. Putting those inside
/// MSBuild is the same collision that cost a library fork. RefCheck is a plain .NET tool run out of
/// process, where all of it is in the box.
/// </para>
/// </remarks>
internal static class RibbonCheck
{
    public const string Extension = ".features.json";

    /// <summary>Checks every manifest in a directory. Never throws.</summary>
    public static IReadOnlyList<RibbonFinding> Check(string directory)
    {
        var findings = new List<RibbonFinding>();

        if (!Directory.Exists(directory))
            return findings;

        foreach (var path in Directory.GetFiles(directory, "*" + Extension).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                CheckManifest(path, directory, findings);
            }
            catch (Exception error)
            {
                findings.Add(new RibbonFinding("RVTRIB000", path,
                    $"the manifest could not be read: {error.Message}"));
            }
        }

        return findings;
    }

    private static void CheckManifest(string path, string directory, List<RibbonFinding> findings)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = document.RootElement;
        var assemblyName = Text(root, "assembly");

        if (assemblyName.Length == 0 || !root.TryGetProperty("buttons", out var buttons))
            return;

        var assemblyPath = Path.Combine(directory, assemblyName);

        if (!File.Exists(assemblyPath))
        {
            findings.Add(new RibbonFinding("RVTRIB001", path,
                $"names the assembly '{assemblyName}', which is not beside it."));
            return;
        }

        // Everything in the same folder: the base class an entry point derives from is another of our
        // assemblies and sits right here. Revit's own assemblies are never needed - an interface is
        // matched by name, and the name is in the reference, not in the file it points at.
        using var facts = TypeFacts.Open(assemblyPath, Directory.GetFiles(directory, "*.dll"));

        if (facts is null)
        {
            findings.Add(new RibbonFinding("RVTRIB000", path,
                $"'{assemblyName}' could not be read as an assembly."));
            return;
        }

        foreach (var button in buttons.EnumerateArray())
            CheckButton(button, path, assemblyName, facts, findings);
    }

    private static void CheckButton(
        JsonElement button,
        string path,
        string assemblyName,
        TypeFacts facts,
        List<RibbonFinding> findings)
    {
        var name = Text(button, "name");
        var className = Text(button, "className");

        if (className.Length == 0)
            return;

        if (!facts.Declares(className))
        {
            findings.Add(new RibbonFinding("RVTRIB001", path,
                $"button '{name}' names the class '{className}', which is not in '{assemblyName}'. " +
                "Revit resolves the name inside that assembly and nowhere else, and says so in a dialog."));
            return;
        }

        if (!facts.Implements(className, TypeFacts.ExternalCommand))
        {
            findings.Add(new RibbonFinding("RVTRIB002", path,
                $"button '{name}' names '{className}', which does not implement IExternalCommand. " +
                "Deriving from CommandEntryPoint<TCommand> is how a feature command gets one."));
        }

        if (!facts.HasAttribute(className, TypeFacts.TransactionAttribute))
        {
            findings.Add(new RibbonFinding("RVTRIB003", path,
                $"'{className}' has no [Transaction]. Revit reads it off the type it constructs - this " +
                "one, not its base and not the command behind it - and refuses the press with a dialog. " +
                "The mode belongs to each command and has no default."));
        }

        var availability = Text(button, "availabilityClassName");

        if (availability.Length == 0)
            return;

        if (!facts.Declares(availability))
        {
            findings.Add(new RibbonFinding("RVTRIB004", path,
                $"button '{name}' names the availability class '{availability}', which is not in " +
                $"'{assemblyName}'. Revit takes the assembly from the button and looks for the class " +
                "inside it, so one in another assembly is never found - and the failure is a " +
                "TypeLoadException dialog, not a greyed-out button."));
            return;
        }

        if (!facts.Implements(availability, TypeFacts.ExternalCommandAvailability))
        {
            findings.Add(new RibbonFinding("RVTRIB005", path,
                $"'{availability}' does not implement IExternalCommandAvailability."));
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
