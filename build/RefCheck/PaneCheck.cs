using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;

namespace BimHouse.RefCheck;

/// <summary>One finding about one dockable pane or its button.</summary>
internal readonly record struct PaneFinding(string Code, string Manifest, string Message);

/// <summary>
/// Checks the dockable panes a manifest declares against the assemblies they name.
/// </summary>
/// <remarks>
/// <para>
/// A pane's content class is a string for the reason a button's class is: the feature assembly it lives
/// in must stay unloaded until Revit first asks for the pane. The host - not Revit - resolves that string,
/// the first time a person opens the pane, and a string that names nothing is found by that person. So the
/// same bargain as the ribbon: what the SDK can see from the item it checks (<c>RVTPAN010</c> to
/// <c>RVTPAN014</c>), and what needs the built assembly is checked here.
/// </para>
/// <list type="bullet">
/// <item><description><b>RVTPAN001</b> - the content assembly is not in the folder, or does not declare the
/// content class.</description></item>
/// <item><description><b>RVTPAN002</b> - the content class is not public, is abstract, an interface or
/// generic, has no public parameterless constructor, or does not implement <c>IPaneContent</c>.</description></item>
/// <item><description><b>RVTPAN003</b> - the content lives in a <c>*.Entry</c> or a <c>*.Declaration</c>
/// assembly. Both load before anybody opens anything - the declaration at startup, the Entry as soon as a
/// tab holding its buttons is shown - so content there would lose the laziness the arrangement exists for,
/// and <c>RVTENT001</c> keeps an Entry assembly to empty classes anyway.</description></item>
/// <item><description><b>RVTPAN004</b> - a button that names a pane has a class that does not derive
/// directly from <c>PaneEntryPoint</c>, or a class in the button's assembly derives from it and no pane
/// button names it: a toggle whose press would find no pane to toggle.</description></item>
/// <item><description><b>RVTPAN005</b> - two panes across the manifests in one folder share an id. The SDK's
/// <c>RVTPAN012</c> sees one project; two features meeting in an edition's folder are seen here. The host
/// asks Revit before registering, so the second is refused at run time - quietly, as far as the person at
/// the screen can tell.</description></item>
/// </list>
/// </remarks>
internal static class PaneCheck
{
    private const string MissingCode = "RVTPAN001";
    private const string ShapeCode = "RVTPAN002";
    private const string PlaceCode = "RVTPAN003";
    private const string ButtonCode = "RVTPAN004";
    private const string DuplicateIdCode = "RVTPAN005";

    /// <summary>Checks every pane in every manifest of a directory. Never throws.</summary>
    /// <param name="directory">The folder to look in; usually a project's output.</param>
    /// <param name="panes">How many panes were checked.</param>
    public static IReadOnlyList<PaneFinding> Check(string directory, out int panes)
    {
        var findings = new List<PaneFinding>();
        var ids = new Dictionary<Guid, string>();
        panes = 0;

        foreach (var path in RibbonCheck.Manifests(directory))
        {
            try
            {
                panes += CheckManifest(path, directory, ids, findings);
            }
            catch (Exception error)
            {
                findings.Add(new PaneFinding(MissingCode, path, $"the manifest could not be read for its panes: {error.Message}"));
            }
        }

        return findings;
    }

    /// <returns>How many panes the manifest declares.</returns>
    private static int CheckManifest(string path, string directory, Dictionary<Guid, string> ids, List<PaneFinding> findings)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = document.RootElement;
        var references = Directory.GetFiles(directory, "*.dll");
        var count = 0;
        var paneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                count++;

                var name = Text(pane, "name");
                paneNames.Add(name);

                if (Guid.TryParse(Text(pane, "id"), out var id))
                {
                    if (ids.TryGetValue(id, out var first))
                    {
                        findings.Add(new PaneFinding(DuplicateIdCode, path,
                            $"pane '{name}' has the id {id}, which '{Path.GetFileName(first)}' gives a pane too. " +
                            "Revit refuses a second registration of one id, and the host asks before registering, " +
                            "so only one of them would ever appear - which one depends on the order the host reads " +
                            "the folder in. A pane's id is chosen once per pane, never copied."));
                    }
                    else
                    {
                        ids[id] = path;
                    }
                }

                CheckContent(pane, name, path, directory, references, findings);
            }
        }

        CheckButtons(root, path, directory, references, paneNames, findings);

        return count;
    }

    private static void CheckContent(
        JsonElement pane,
        string name,
        string path,
        string directory,
        string[] references,
        List<PaneFinding> findings)
    {
        var assemblyName = Text(pane, "contentAssembly");
        var className = Text(pane, "contentClassName");

        if (assemblyName.Length == 0 || className.Length == 0)
            return;

        if (assemblyName.EndsWith(".Entry", StringComparison.OrdinalIgnoreCase)
            || assemblyName.EndsWith(".Declaration", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PaneFinding(PlaceCode, path,
                $"pane '{name}' puts its content in '{assemblyName}'. A declaration loads at startup and an Entry " +
                "assembly as soon as the tab holding its buttons is shown - both before anybody opens the pane - so " +
                "the content would load with them, and WPF-UI with it. Put the content class in the feature's own " +
                "assembly, the one that loads on a press."));
        }

        var assemblyPath = Path.Combine(directory, assemblyName + ".dll");

        if (!File.Exists(assemblyPath))
        {
            findings.Add(new PaneFinding(MissingCode, path,
                $"pane '{name}' names its content in '{assemblyName}', and '{assemblyName}.dll' is not beside the " +
                "manifest. The host refuses to register a pane whose content it could never load; the pane would " +
                "simply be missing, with one line in the log."));
            return;
        }

        using var facts = TypeFacts.Open(assemblyPath, references);

        if (facts is null)
        {
            findings.Add(new PaneFinding(MissingCode, path, $"'{assemblyName}.dll' could not be read as an assembly."));
            return;
        }

        if (facts.Definition(className) is not { } type)
        {
            findings.Add(new PaneFinding(MissingCode, path,
                $"pane '{name}' names the content class '{className}', which is not in '{assemblyName}'. The host " +
                "resolves it there the first time the pane is opened, and a pane whose content cannot be found " +
                "says it stopped - in front of the person who opened it."));
            return;
        }

        var problems = new List<string>();
        var attributes = type.Attributes;

        if (type.IsNested || (attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
            problems.Add("it is not a public top-level class");

        if ((attributes & TypeAttributes.Interface) != 0)
            problems.Add("it is an interface");
        else if ((attributes & TypeAttributes.Abstract) != 0)
            problems.Add("it is abstract");

        if (type.GetGenericParameters().Count > 0)
            problems.Add("it is generic");

        if (!HasPublicParameterlessConstructor(facts.Reader, type))
            problems.Add("it has no public parameterless constructor");

        if (!facts.Implements(className, TypeFacts.PaneContent))
            problems.Add("it does not implement IPaneContent");

        if (problems.Count > 0)
        {
            findings.Add(new PaneFinding(ShapeCode, path,
                $"pane '{name}' names '{className}' as its content, and {string.Join("; ", problems)}. The host " +
                "constructs it with Activator.CreateInstance and casts it to IPaneContent, so each of these is a " +
                "pane that stops the moment it is opened."));
        }
    }

    private static void CheckButtons(
        JsonElement root,
        string path,
        string directory,
        string[] references,
        HashSet<string> paneNames,
        List<PaneFinding> findings)
    {
        var assemblyName = Text(root, "assembly");

        if (assemblyName.Length == 0)
            return;

        var assemblyPath = Path.Combine(directory, assemblyName);

        // RVTRIB001 already says it when the assembly is missing; nothing to add here.
        if (!File.Exists(assemblyPath))
            return;

        using var facts = TypeFacts.Open(assemblyPath, references);

        if (facts is null)
            return;

        var toggles = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
        {
            foreach (var button in buttons.EnumerateArray())
            {
                var pane = Text(button, "pane");
                var className = Text(button, "className");

                if (pane.Length == 0 || className.Length == 0)
                    continue;

                toggles.Add(className);

                // Existence is RVTRIB001's; this is only about what an existing class derives from.
                if (facts.Definition(className) is not { } type)
                    continue;

                if (!DerivesFromPaneEntryPoint(facts, type))
                {
                    findings.Add(new PaneFinding(ButtonCode, path,
                        $"button '{Text(button, "name")}' toggles the pane '{pane}' and names '{className}', which does " +
                        "not derive directly from PaneEntryPoint. Only that base finds the pane by its button's class " +
                        "name; any other command behind this button would run and leave the pane where it was."));
                }
            }
        }

        foreach (var handle in facts.Reader.TypeDefinitions)
        {
            var type = facts.Reader.GetTypeDefinition(handle);

            if (!DerivesFromPaneEntryPoint(facts, type))
                continue;

            var name = TypeFacts.DefinitionName(facts.Reader, type);

            if (toggles.Contains(name))
                continue;

            findings.Add(new PaneFinding(ButtonCode, path,
                $"'{name}' derives from PaneEntryPoint, and no button in this manifest that names a pane names it. " +
                "A pane's button is found by its class, so this class, pressed, would find no pane - add Pane to the " +
                $"button that names it{(paneNames.Count > 0 ? $" (this manifest declares {string.Join(", ", paneNames)})" : string.Empty)}, " +
                "or remove the class."));
        }
    }

    private static bool DerivesFromPaneEntryPoint(TypeFacts facts, TypeDefinition type)
    {
        var baseType = facts.Decode(type.BaseType);

        return baseType is not null
               && baseType.Arguments.Count == 0
               && string.Equals(baseType.Assembly, TypeFacts.Abstractions, StringComparison.OrdinalIgnoreCase)
               && baseType.FullName == TypeFacts.PaneEntryPoint;
    }

    private static bool HasPublicParameterlessConstructor(MetadataReader reader, TypeDefinition type)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);

            if (reader.GetString(method.Name) != ".ctor"
                || (method.Attributes & MethodAttributes.Static) != 0
                || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                continue;

            try
            {
                if (method.DecodeSignature(ParameterCounter.Instance, null).ParameterTypes.Length == 0)
                    return true;
            }
            catch (BadImageFormatException)
            {
                // Unreadable is not public and parameterless as far as anyone can tell.
            }
        }

        return false;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
