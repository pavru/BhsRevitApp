using System.Text.Json;

namespace BimHouse.RefCheck;

/// <summary>One finding about one button.</summary>
internal readonly record struct RibbonFinding(string Code, string Manifest, string Message);

/// <summary>What the ribbon checks looked at in one directory, so a pass can say so.</summary>
/// <param name="Manifests">Manifests read.</param>
/// <param name="Buttons">Buttons across them.</param>
/// <param name="EntryManifests">How many of the manifests are a feature's <c>&lt;P&gt;.Entry.features.json</c>.</param>
internal readonly record struct RibbonTally(int Manifests, int Buttons, int EntryManifests);

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
/// Three more are not dialogs anybody has seen, and they exist because buttons stopped coming from one
/// project. A feature's buttons now arrive in an edition's folder from the feature's Entry project, as a
/// related file of its assembly, and that move opened failures nothing looked at before:
/// </para>
/// <list type="bullet">
/// <item><description><b>RVTRIB006</b> - two buttons share a name across the manifests in one folder.
/// The SDK's <c>RVTRIB011</c> sees one project's items and cannot see two projects meet. RevitAPIUI.xml
/// for 2024 and 2027 documents that <c>RibbonPanel.AddItem</c> throws <c>ArgumentException</c> when the
/// panel already holds an item of that name, and the host logs a throw and moves on - so on a shared
/// panel the second button is simply missing. Not measured in Revit; and the check does not ask whether
/// the two land on the same panel, because a name that is unique only by accident of layout stops being
/// unique the day a feature moves a button.</description></item>
/// <item><description><b>RVTRIB007</b> - an Application add-in with a feature's Entry assembly beside it
/// and not that assembly's manifest, or with no manifest beside it at all. A related-file copy that
/// stopped happening fails in silence and ships an add-in without that feature's buttons; this is the
/// only place that can notice. Keyed on each Entry assembly rather than on the manifest count, because an
/// add-in with buttons of its own always has a manifest to count.</description></item>
/// <item><description><b>RVTENT003</b> - a button in a feature's Entry manifest names a tab. The feature
/// chooses the panel and the edition the tab; the host puts the button on the edition's tab regardless
/// and logs a warning, so a tab written here only says something untrue about where it goes.</description></item>
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

    /// <summary>How a feature's Entry manifest ends; the same contract the host reads by.</summary>
    public const string EntryExtension = ".Entry" + Extension;

    private const string DuplicateNameCode = "RVTRIB006";
    private const string NoManifestCode = "RVTRIB007";
    private const string EntryTabCode = "RVTENT003";

    /// <summary>Checks every manifest in a directory. Never throws.</summary>
    public static IReadOnlyList<RibbonFinding> Check(string directory, out RibbonTally tally)
    {
        var findings = new List<RibbonFinding>();
        tally = default;

        if (!Directory.Exists(directory))
            return findings;

        // Across every manifest in the folder, not per file: the question is what the host will be asked
        // to build, and it builds from all of them at once.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifests = 0;
        var buttons = 0;
        var entryManifests = 0;

        foreach (var path in Manifests(directory))
        {
            manifests++;

            if (IsEntryManifest(path))
                entryManifests++;

            try
            {
                buttons += CheckManifest(path, directory, names, findings);
            }
            catch (Exception error)
            {
                findings.Add(new RibbonFinding("RVTRIB000", path,
                    $"the manifest could not be read: {error.Message}"));
            }
        }

        tally = new RibbonTally(manifests, buttons, entryManifests);
        return findings;
    }

    /// <summary>
    /// Checks that an Application add-in has buttons to build: a manifest beside each Entry assembly
    /// beside it, and a manifest at all. Never throws.
    /// </summary>
    /// <param name="directory">The folder the add-in assembly is in.</param>
    /// <param name="applicationPath">The add-in assembly an Application manifest entry names.</param>
    /// <remarks>
    /// Only for an Application: a DBApplication has no ribbon by definition, and the build passes only
    /// the assemblies named by an Application entry. This is a statement about the add-in as it is
    /// today - every Application in this repository has buttons - and an edition built deliberately
    /// without any would be the day this rule is revisited by name, not switched off.
    /// </remarks>
    public static IReadOnlyList<RibbonFinding> CheckApplication(string directory, string applicationPath)
    {
        var findings = new List<RibbonFinding>();
        var application = Path.GetFileName(applicationPath);

        // Per Entry assembly first, and that is a correction. The first version asked only whether the
        // folder held any manifest at all, and the probe - which ships one of its own - passed with its
        // Entry manifest deleted: reproduced on a copy of its output, where RefCheck printed that the
        // add-in had "no Entry manifest to match against Modules" and exited 0. The host then builds no
        // button for that feature and logs nothing, because there is no manifest for it to skip.
        foreach (var entry in EntryCheck.EntryAssemblies(directory))
        {
            var expected = Path.GetFileNameWithoutExtension(entry) + Extension;

            if (File.Exists(Path.Combine(directory, expected)))
                continue;

            findings.Add(new RibbonFinding(NoManifestCode, applicationPath,
                $"'{Path.GetFileName(entry)}' lies beside the Application add-in '{application}' and " +
                $"'{expected}' does not. An Entry assembly exists to carry a feature's buttons, and its " +
                "manifest reaches an edition's folder as a related file of it - " +
                "AllowedReferenceRelatedFileExtensions in Directory.Build.targets. When that copy stops " +
                "happening nothing fails: the host has no manifest to build or to skip, so the feature's " +
                "buttons are missing and the log says nothing about them."));
        }

        if (findings.Count == 0 && Manifests(directory).Count == 0)
        {
            findings.Add(new RibbonFinding(NoManifestCode, applicationPath,
                $"'{Path.GetFileName(applicationPath)}' is an Application add-in and no *{Extension} lies " +
                "beside it, so the host has nothing to build a ribbon from. A feature's manifest reaches an " +
                "edition's folder as a related file of the Entry assembly it references - " +
                "AllowedReferenceRelatedFileExtensions in Directory.Build.targets - and when that copy " +
                "stops happening nothing fails: the add-in loads, starts and shows no buttons."));
        }

        return findings;
    }

    /// <summary>Whether a path is a feature's Entry manifest rather than an edition's own.</summary>
    public static bool IsEntryManifest(string path) =>
        Path.GetFileName(path).EndsWith(EntryExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every manifest in a directory, in a stable order.</summary>
    public static IReadOnlyList<string> Manifests(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*" + Extension)
                .Where(path => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <returns>How many buttons the manifest declares.</returns>
    private static int CheckManifest(
        string path,
        string directory,
        Dictionary<string, string> names,
        List<RibbonFinding> findings)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = document.RootElement;
        var assemblyName = Text(root, "assembly");

        if (!root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        var entry = IsEntryManifest(path);

        // Before the assembly is looked at: two buttons with one name are a problem whether or not the
        // assembly behind either of them is where it should be.
        foreach (var button in buttons.EnumerateArray())
        {
            count++;

            var name = Text(button, "name");

            if (name.Length > 0)
            {
                if (names.TryGetValue(name, out var first))
                {
                    findings.Add(new RibbonFinding(DuplicateNameCode, path,
                        $"button '{name}' is declared here and in '{Path.GetFileName(first)}'. The host builds " +
                        "every manifest in this folder into one ribbon, and RevitAPIUI.xml documents that " +
                        "RibbonPanel.AddItem throws when the panel already holds an item of that name - the " +
                        "host logs it, and the second button is missing. RVTRIB011 in the SDK catches this " +
                        "inside one project, not two projects meeting in an edition's folder. Rename one of " +
                        "them in its project file."));
                }
                else
                {
                    names[name] = path;
                }
            }

            var tab = Text(button, "tab");

            if (entry && tab.Length > 0)
            {
                findings.Add(new RibbonFinding(EntryTabCode, path,
                    $"button '{name}' names the tab '{tab}', and this is a feature's Entry manifest. The feature " +
                    "chooses the panel and the edition chooses the tab: the host puts this button on the tab " +
                    "the edition names whatever is written here, and logs a warning - so the Tab in the " +
                    "project file says something untrue about where the button goes. Remove it; the edition " +
                    "names its tab in RibbonTab."));
            }
        }

        if (assemblyName.Length == 0)
            return count;

        var assemblyPath = Path.Combine(directory, assemblyName);

        if (!File.Exists(assemblyPath))
        {
            findings.Add(new RibbonFinding("RVTRIB001", path,
                $"names the assembly '{assemblyName}', which is not beside it."));
            return count;
        }

        // Everything in the same folder: the base class an entry point derives from is another of our
        // assemblies and sits right here. Revit's own assemblies are never needed - an interface is
        // matched by name, and the name is in the reference, not in the file it points at.
        using var facts = TypeFacts.Open(assemblyPath, Directory.GetFiles(directory, "*.dll"));

        if (facts is null)
        {
            findings.Add(new RibbonFinding("RVTRIB000", path,
                $"'{assemblyName}' could not be read as an assembly."));
            return count;
        }

        foreach (var button in buttons.EnumerateArray())
            CheckButton(button, path, assemblyName, facts, findings);

        return count;
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
                "Deriving from CommandEntryPoint<TFeature, TCommand> in the feature's Entry assembly is how " +
                "a feature command gets one; CommandEntryPoint<TCommand> is kept for the probe's control."));
        }

        if (!facts.HasAttribute(className, TypeFacts.TransactionAttribute))
        {
            findings.Add(new RibbonFinding("RVTRIB003", path,
                $"'{className}' has no [Transaction]. Revit reads it off the type it constructs - this " +
                "one, not the command behind it - and refuses the press with a dialog. Declare it here: " +
                "the transaction mode is a property of each command and has no default."));
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
