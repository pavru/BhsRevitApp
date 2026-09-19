using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace BHS.Revit.Sdk;

/// <summary>
///     Writes the ribbon manifest an edition ships beside its assembly.
/// </summary>
/// <remarks>
///     <para>
///     The manifest exists so that the ribbon can be built from <b>strings</b>. Naming a button's class
///     with <c>typeof</c> loads that class, which resolves its base, which loads the feature assembly -
///     while the ribbon is being built, which is the one thing the whole arrangement avoids. Measured:
///     with <c>typeof</c> the feature was loaded before any button had been pressed. Nothing in the
///     language objects to writing it, so the only reliable way not to is to have no opportunity.
///     </para>
///     <para>
///     The price of strings is that mistakes move from compile time to a modal dialog in front of a
///     person. Buying that back means reading the built assembly as metadata, and that belongs to
///     RefCheck rather than here: doing it inside MSBuild would mean shipping a metadata reader and
///     its five .NET Framework polyfills into the build process - the same collision this repository
///     forked a library over. So this task writes the manifest and checks what it can see from the
///     item alone; <c>RVTRIB001</c> to <c>RVTRIB005</c> are checked against the assembly afterwards.
///     </para>
///     <para>
///     Required metadata on a <c>RevitRibbonButton</c> item: <c>Panel</c>, <c>Text</c>, <c>ClassName</c>.
///     The item's identity is the button's internal name. Optional: <c>Tab</c> (default is Revit's
///     Add-Ins tab), <c>ToolTip</c>, <c>LongDescription</c>, <c>AvailabilityClassName</c>, <c>Order</c>,
///     and <c>Pane</c> - the name of a <c>RevitDockablePane</c> in the same project, which the button
///     shows and hides.
///     </para>
///     <para>
///     <b>Dockable panes, from 1.6.0.</b> A <c>RevitDockablePane</c> item is registered by the host at
///     startup, and its content class is constructed by the host - not by Revit - the first time Revit
///     asks for the pane. So the content class is a string here for the same reason a button's class is:
///     the feature assembly it lives in must stay unloaded until then. Required: <c>Id</c> (a GUID, fixed
///     forever - Revit keeps the dock layout a user arranged under it), <c>Title</c>,
///     <c>ContentAssembly</c> (a simple name) and <c>ContentClassName</c>. Optional: <c>DockPosition</c>
///     (Left, Right, Top, Bottom; default Right), <c>MinimumWidth</c>, <c>MinimumHeight</c>,
///     <c>EditorInteraction</c> (Dismiss or KeepAlive; default Dismiss) and <c>VisibleByDefault</c>
///     (default false). Floating and Tabbed are refused: the first needs a rectangle and the second
///     another pane's id, and neither has a consumer. What can be seen from the item is checked here,
///     <c>RVTPAN010</c> to <c>RVTPAN014</c>; what needs the built assembly - is the content class there,
///     is it an <c>IPaneContent</c>, does the button's class show panes - is RefCheck's <c>RVTPAN001</c>
///     to <c>RVTPAN005</c>.
///     </para>
///     <para>
///     <b>Declaration strings, from 1.6.5.</b> A project that declares one <c>RevitDeclarationStrings</c>
///     item - a neutral <c>.resx</c> - turns the values of <c>Text</c>, <c>ToolTip</c> and
///     <c>LongDescription</c> on its buttons and <c>Title</c> on its panes into keys of that file. The
///     neutral manifest carries the resolved text, in the same shape as before; each
///     <c>&lt;name&gt;.&lt;culture&gt;.resx</c> beside it becomes <c>&lt;assembly&gt;.features.&lt;culture&gt;.json</c>,
///     keyed by item name and never by position, holding only what that culture translates. The host
///     overlays the file for Revit's language on the neutral one. <c>RVTRIB012</c>: a key in a culture file
///     that the neutral file lacks - the orphan an edit of the neutral file leaves behind, which nothing
///     would ever ask for. <c>RVTRIB013</c>: an item names a key the neutral file lacks. <c>RVTRIB014</c>:
///     the strings file itself is wrong - more than one, unreadable, or a culture outside
///     <c>RevitDeclarationCultures</c>, whose overlay would be written and never reach an edition.
///     </para>
/// </remarks>
[PublicAPI]
public class GenerateFeatureManifest : Task
{
    /// <summary>The values of Revit's <c>DockPosition</c> a pane may start in.</summary>
    /// <remarks>
    /// Floating needs a rectangle and Tabbed needs the id of the pane to sit behind - which CLAUDE.md
    /// makes the edition's choice rather than the feature's - so both are refused until somebody needs them.
    /// </remarks>
    private static readonly string[] DockPositions = { "Left", "Right", "Top", "Bottom" };

    private static readonly string[] EditorInteractions = { "Dismiss", "KeepAlive" };

    private static readonly string[] Booleans = { "true", "false" };

    /// <summary>The buttons this project declares. Not required: a project may declare only panes.</summary>
    public ITaskItem[]? Buttons { get; set; }

    /// <summary>The dockable panes this project declares. Optional.</summary>
    public ITaskItem[]? Panes { get; set; }

    /// <summary>Where to write the manifest.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>The file name of the assembly the buttons live in, as the ribbon will name it.</summary>
    [Required]
    public string AssemblyFileName { get; set; } = string.Empty;

    /// <summary>The neutral declaration strings file, at most one. Optional.</summary>
    public ITaskItem[]? Strings { get; set; }

    /// <summary>
    /// The cultures an overlay may be written for, separated by semicolons - <c>RevitDeclarationCultures</c>.
    /// Empty accepts every culture file.
    /// </summary>
    public string Cultures { get; set; } = string.Empty;

    /// <summary>The overlays written, so that a clean removes them.</summary>
    [Output]
    public ITaskItem[] CultureFiles { get; private set; } = Array.Empty<ITaskItem>();

    /// <summary>The metadata that hold interface text, and so become keys when strings are declared.</summary>
    private static readonly string[] ButtonTexts = { "Text", "ToolTip", "LongDescription" };

    private static readonly string[] PaneTexts = { "Title" };

    public override bool Execute()
    {
        var buttons = Buttons ?? Array.Empty<ITaskItem>();
        var panes = Panes ?? Array.Empty<ITaskItem>();

        if (buttons.Length == 0 && panes.Length == 0)
        {
            Log.LogMessage(MessageImportance.Low, "No RevitRibbonButton or RevitDockablePane items; no feature manifest written.");
            return true;
        }

        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paneIds = new HashSet<Guid>();
            var ok = true;

            // Panes first, so that a button naming one can be checked against the complete list.
            foreach (var pane in panes)
            {
                if (!ValidatePane(pane, paneNames, paneIds))
                    ok = false;
            }

            foreach (var button in buttons)
            {
                if (!Validate(button, names, paneNames))
                    ok = false;
            }

            if (!ok)
                return false;

            var strings = LoadStrings(buttons, panes, out var overlays);

            if (strings is null)
                return false;

            Write(buttons, panes, strings.Resolve);
            WriteOverlays(buttons, panes, overlays);

            // Said loudly when there are panes: a check that passes and leaves no trace in a minimal log
            // is indistinguishable from one that stopped running, which RefCheck taught this repository.
            Log.LogMessage(panes.Length > 0 ? MessageImportance.High : MessageImportance.Normal,
                $"Wrote the feature manifest for {buttons.Length} button(s) and {panes.Length} dockable pane(s) " +
                $"to {OutputPath}" + (panes.Length > 0 ? "; RVTPAN010-014 passed" : string.Empty));

            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception);
            return false;
        }
    }

    private bool Validate(ITaskItem button, HashSet<string> names, HashSet<string> paneNames)
    {
        var name = button.ItemSpec;
        var ok = true;

        if (!names.Add(name))
        {
            Log.LogError($"RVTRIB011: two ribbon buttons are called '{name}'. Revit files a button by " +
                         "name, so the second would replace the first.");
            ok = false;
        }

        foreach (var required in new[] { "Panel", "Text", "ClassName" })
        {
            if (string.IsNullOrWhiteSpace(button.GetMetadata(required)))
            {
                Log.LogError($"RVTRIB010: ribbon button '{name}' has no '{required}'.");
                ok = false;
            }
        }

        var pane = button.GetMetadata("Pane");

        if (pane.Length > 0 && !paneNames.Contains(pane))
        {
            Log.LogError($"RVTPAN014: ribbon button '{name}' shows the pane '{pane}', and this project declares " +
                         "no RevitDockablePane of that name. The host would build the button, and its press " +
                         "would find no pane to show.");
            ok = false;
        }

        return ok;
    }

    private bool ValidatePane(ITaskItem pane, HashSet<string> names, HashSet<Guid> ids)
    {
        var name = pane.ItemSpec;
        var ok = true;

        if (!names.Add(name))
        {
            Log.LogError($"RVTPAN012: two dockable panes are called '{name}'. A button names its pane by this, " +
                         "so the second could never be reached.");
            ok = false;
        }

        foreach (var required in new[] { "Id", "Title", "ContentAssembly", "ContentClassName" })
        {
            if (string.IsNullOrWhiteSpace(pane.GetMetadata(required)))
            {
                Log.LogError($"RVTPAN010: dockable pane '{name}' has no '{required}'.");
                ok = false;
            }
        }

        var id = pane.GetMetadata("Id");

        if (id.Length > 0)
        {
            if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
            {
                Log.LogError($"RVTPAN011: dockable pane '{name}' has the id '{id}', which is not a GUID other " +
                             "than the empty one. Revit keeps the dock layout a user arranged under this id, so " +
                             "it is chosen once and never changed.");
                ok = false;
            }
            else if (!ids.Add(guid))
            {
                Log.LogError($"RVTPAN012: dockable pane '{name}' has the id {guid}, which another pane in this " +
                             "project already has. Revit refuses the second registration.");
                ok = false;
            }
        }

        ok &= OneOf(pane, "DockPosition", DockPositions);
        ok &= OneOf(pane, "EditorInteraction", EditorInteractions);
        ok &= OneOf(pane, "VisibleByDefault", Booleans);
        ok &= Size(pane, "MinimumWidth");
        ok &= Size(pane, "MinimumHeight");

        return ok;
    }

    private bool OneOf(ITaskItem pane, string metadata, string[] allowed)
    {
        var value = pane.GetMetadata(metadata);

        if (value.Length == 0)
            return true;

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        Log.LogError($"RVTPAN013: dockable pane '{pane.ItemSpec}' has {metadata} '{value}'; expected one of " +
                     $"{string.Join(", ", allowed)}.");
        return false;
    }

    private bool Size(ITaskItem pane, string metadata)
    {
        var value = pane.GetMetadata(metadata);

        if (value.Length == 0
            || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0))
            return true;

        Log.LogError($"RVTPAN013: dockable pane '{pane.ItemSpec}' has {metadata} '{value}'; expected a positive " +
                     "whole number of device-independent units.");
        return false;
    }

    /// <summary>What the strings file resolves to, and the overlays to write beside the manifest.</summary>
    private sealed class StringSet
    {
        public StringSet(DeclarationStrings? neutral) => Neutral = neutral;

        public DeclarationStrings? Neutral { get; }

        /// <summary>The text for one metadata value: itself without a strings file, its key's text with one.</summary>
        /// <remarks>Only called after <c>RVTRIB013</c> has passed, so a key here is always there.</remarks>
        public string Resolve(ITaskItem item, string metadata)
        {
            var value = item.GetMetadata(metadata);

            return Neutral is null || value.Length == 0 ? value : Neutral.Values[value];
        }
    }

    /// <summary>One overlay: a culture, its strings, and where its json goes.</summary>
    private sealed class Overlay
    {
        public Overlay(string culture, DeclarationStrings strings, string outputPath)
        {
            Culture = culture;
            Strings = strings;
            OutputPath = outputPath;
        }

        public string Culture { get; }

        public DeclarationStrings Strings { get; }

        public string OutputPath { get; }
    }

    /// <summary>
    /// Reads the strings file and its cultures and checks them against the items. Null when a check failed,
    /// every failure already logged.
    /// </summary>
    private StringSet? LoadStrings(ITaskItem[] buttons, ITaskItem[] panes, out List<Overlay> overlays)
    {
        overlays = new List<Overlay>();
        var files = Strings ?? Array.Empty<ITaskItem>();

        if (files.Length == 0)
            return new StringSet(null);

        if (files.Length > 1)
        {
            Log.LogError($"RVTRIB014: {files.Length} RevitDeclarationStrings items are declared, and a project has one " +
                         "neutral strings file - the keys of its buttons and panes are looked up in exactly one place.");
            return null;
        }

        var path = files[0].GetMetadata("FullPath");
        DeclarationStrings neutral;

        try
        {
            neutral = DeclarationStrings.Read(path);
        }
        catch (Exception error)
        {
            Log.LogError($"RVTRIB014: the declaration strings file '{path}' could not be read: {error.Message}");
            return null;
        }

        var fileName = Path.GetFileName(path);
        var ok = true;
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (item, kind, metadata) in Texts(buttons, panes))
        {
            var key = item.GetMetadata(metadata);

            if (key.Length == 0)
                continue;

            used.Add(key);

            if (neutral.Values.ContainsKey(key))
                continue;

            Log.LogError($"RVTRIB013: {kind} '{item.ItemSpec}' has {metadata} '{key}', and '{fileName}' has no such " +
                         "key. With a RevitDeclarationStrings file declared, every Text, ToolTip, LongDescription and " +
                         $"Title is a key into it; add '{key}' to '{fileName}', or correct the key.");
            ok = false;
        }

        var allowed = new HashSet<string>(
            Cultures.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        var stem = OutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? OutputPath.Substring(0, OutputPath.Length - ".json".Length)
            : OutputPath;

        foreach (var pair in DeclarationStrings.CulturesBeside(path))
        {
            var culture = CultureInfo.GetCultureInfo(pair.Key).Name;
            var cultureFile = Path.GetFileName(pair.Value);

            if (allowed.Count > 0 && !allowed.Contains(culture))
            {
                Log.LogError($"RVTRIB014: '{cultureFile}' translates the declaration into {culture}, which is not in " +
                             $"RevitDeclarationCultures ({Cultures.Trim()}). Its overlay would be written beside this " +
                             "assembly and never reach an edition's folder - the related file extensions an edition " +
                             "copies are derived from that list. Add the culture there, or remove the file.");
                ok = false;
                continue;
            }

            DeclarationStrings strings;

            try
            {
                strings = DeclarationStrings.Read(pair.Value);
            }
            catch (Exception error)
            {
                Log.LogError($"RVTRIB014: the declaration strings file '{pair.Value}' could not be read: {error.Message}");
                ok = false;
                continue;
            }

            // One direction only. A culture file is an overlay, and en-GB differs from the neutral file by a
            // handful of words, so demanding equal key sets would make a correct sparse file a build error -
            // fixed by copying neutral strings, which then look translated and never were. What this side
            // catches is the orphan: a key renamed in the neutral file and left behind here, silently
            // never asked for again.
            foreach (var key in strings.Values.Keys)
            {
                if (neutral.Values.ContainsKey(key))
                    continue;

                Log.LogError($"RVTRIB012: '{cultureFile}' has the key '{key}', and '{fileName}' does not. A culture file " +
                             "only overlays the neutral one, so this string can never be shown - most likely the key " +
                             $"was renamed in '{fileName}' and left behind here. Rename or remove it.");
                ok = false;
            }

            overlays.Add(new Overlay(culture, strings, stem + "." + culture + ".json"));
        }

        if (!ok)
            return null;

        // Said when it passes, and with what it counted: a check that leaves no trace is indistinguishable
        // from one that stopped running. The untranslated count is a line, not a failure - an overlay is
        // sparse by design, and a missing translation shows itself in English, on its own place.
        var cultures = overlays.Count == 0
            ? "no culture file beside it"
            : string.Join(", ", overlays.ConvertAll(overlay =>
                $"{overlay.Culture} translates {Translated(overlay, used)} of {used.Count} used key(s)"));

        Log.LogMessage(MessageImportance.High,
            $"Declaration strings: {used.Count} key(s) from '{fileName}' resolved for {buttons.Length} button(s) and " +
            $"{panes.Length} pane(s) - RVTRIB013 passed; {cultures} - RVTRIB012 passed.");

        return new StringSet(neutral);
    }

    private static int Translated(Overlay overlay, HashSet<string> used)
    {
        var count = 0;

        foreach (var key in used)
        {
            if (overlay.Strings.Values.ContainsKey(key))
                count++;
        }

        return count;
    }

    /// <summary>Every metadata value that holds interface text, with what kind of item it is on.</summary>
    private static IEnumerable<(ITaskItem Item, string Kind, string Metadata)> Texts(ITaskItem[] buttons, ITaskItem[] panes)
    {
        foreach (var button in buttons)
        {
            foreach (var metadata in ButtonTexts)
                yield return (button, "ribbon button", metadata);
        }

        foreach (var pane in panes)
        {
            foreach (var metadata in PaneTexts)
                yield return (pane, "dockable pane", metadata);
        }
    }

    /// <summary>
    /// Writes one overlay per culture, keyed by item name, and removes overlays this build no longer makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>By name, never by position.</b> The reader on the other side flattens arrays by ordinal, and a
    /// sparse overlay laid over the neutral array by position would put one button's text on another the
    /// first time an item is added in the middle. Names are what the host builds by anyway.
    /// </para>
    /// <para>
    /// Only what the culture translates is written: an item with nothing translated is left out, and so is
    /// each field that has no translation. The host falls back to the neutral text for everything absent.
    /// </para>
    /// <para>
    /// A stale overlay is removed because it would be delivered: an edition copies every related file it
    /// finds beside the Entry assembly, and a translation withdrawn here must not keep showing there.
    /// </para>
    /// </remarks>
    private void WriteOverlays(ITaskItem[] buttons, ITaskItem[] panes, List<Overlay> overlays)
    {
        var written = new List<ITaskItem>();

        foreach (var overlay in overlays)
        {
            var json = new StringBuilder();

            json.Append("{\n");
            json.Append("  // Generated by BHS.Revit.Sdk from the declaration strings. Edit the .resx instead.\n");
            json.Append("  \"version\": \"2\",\n");
            json.Append("  \"culture\": ").Append(Quote(overlay.Culture)).Append(",\n");
            json.Append("  \"buttons\": {\n");
            Items(json, buttons, ButtonTexts, overlay);
            json.Append("  },\n");
            json.Append("  \"panes\": {\n");
            Items(json, panes, PaneTexts, overlay);
            json.Append("  }\n");
            json.Append("}\n");

            File.WriteAllText(overlay.OutputPath, json.ToString(), new UTF8Encoding(false));
            written.Add(new Microsoft.Build.Utilities.TaskItem(overlay.OutputPath));
        }

        CultureFiles = written.ToArray();

        var directory = Path.GetDirectoryName(Path.GetFullPath(OutputPath)) ?? ".";
        var pattern = Path.GetFileNameWithoutExtension(OutputPath) + ".*.json";

        foreach (var file in Directory.GetFiles(directory, pattern))
        {
            if (written.Exists(item => string.Equals(Path.GetFullPath(item.ItemSpec), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
                continue;

            File.Delete(file);
            Log.LogMessage(MessageImportance.Normal, $"Removed the overlay {file}, which this build no longer makes.");
        }

    }

    private static void Items(StringBuilder json, ITaskItem[] items, string[] fields, Overlay overlay)
    {
        var entries = new List<string>();

        foreach (var item in items)
        {
            var translated = new List<string>();

            foreach (var metadata in fields)
            {
                var key = item.GetMetadata(metadata);

                if (key.Length > 0 && overlay.Strings.Values.TryGetValue(key, out var value))
                    translated.Add("      " + Quote(JsonName(metadata)) + ": " + Quote(value));
            }

            if (translated.Count > 0)
                entries.Add("    " + Quote(item.ItemSpec) + ": {\n" + string.Join(",\n", translated) + "\n    }");
        }

        if (entries.Count > 0)
            json.Append(string.Join(",\n", entries)).Append('\n');
    }

    /// <summary>The name a field has in the json: the metadata name with a lower-case first letter.</summary>
    private static string JsonName(string metadata) =>
        char.ToLowerInvariant(metadata[0]) + metadata.Substring(1);

    private void Write(ITaskItem[] buttons, ITaskItem[] panes, Func<ITaskItem, string, string> text)
    {
        var json = new StringBuilder();

        // Version 2 adds "panes" and a button's "pane". The host reads both; a host from before reading
        // this file ignores the keys it does not know, which is what a flat key -> string reader does.
        json.Append("{\n");
        json.Append("  // Generated by BHS.Revit.Sdk. Edit the RevitRibbonButton and RevitDockablePane items instead.\n");
        json.Append("  \"version\": \"2\",\n");
        json.Append("  \"assembly\": ").Append(Quote(AssemblyFileName)).Append(",\n");
        json.Append("  \"buttons\": [\n");

        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];

            json.Append("    {\n");
            Field(json, "name", button.ItemSpec, last: false);
            Field(json, "tab", button.GetMetadata("Tab"), last: false);
            Field(json, "panel", button.GetMetadata("Panel"), last: false);
            Field(json, "text", text(button, "Text"), last: false);
            Field(json, "toolTip", text(button, "ToolTip"), last: false);
            Field(json, "longDescription", text(button, "LongDescription"), last: false);
            Field(json, "availabilityClassName", button.GetMetadata("AvailabilityClassName"), last: false);
            Field(json, "order", button.GetMetadata("Order"), last: false);
            Field(json, "pane", button.GetMetadata("Pane"), last: false);
            Field(json, "className", button.GetMetadata("ClassName"), last: true);
            json.Append(index == buttons.Length - 1 ? "    }\n" : "    },\n");
        }

        json.Append("  ],\n");
        json.Append("  \"panes\": [\n");

        for (var index = 0; index < panes.Length; index++)
        {
            var pane = panes[index];

            json.Append("    {\n");
            Field(json, "name", pane.ItemSpec, last: false);
            // Normalised, so that the host and RefCheck compare one spelling of one id.
            Field(json, "id", Guid.Parse(pane.GetMetadata("Id")).ToString("D"), last: false);
            Field(json, "title", text(pane, "Title"), last: false);
            Field(json, "contentAssembly", pane.GetMetadata("ContentAssembly"), last: false);
            Field(json, "contentClassName", pane.GetMetadata("ContentClassName"), last: false);
            Field(json, "dockPosition", pane.GetMetadata("DockPosition"), last: false);
            Field(json, "minimumWidth", pane.GetMetadata("MinimumWidth"), last: false);
            Field(json, "minimumHeight", pane.GetMetadata("MinimumHeight"), last: false);
            Field(json, "editorInteraction", pane.GetMetadata("EditorInteraction"), last: false);
            Field(json, "visibleByDefault", pane.GetMetadata("VisibleByDefault"), last: true);
            json.Append(index == panes.Length - 1 ? "    }\n" : "    },\n");
        }

        json.Append("  ]\n");
        json.Append("}\n");

        var directory = Path.GetDirectoryName(OutputPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputPath, json.ToString(), new UTF8Encoding(false));
    }

    private static void Field(StringBuilder text, string name, string value, bool last)
    {
        text.Append("      ").Append(Quote(name)).Append(": ").Append(Quote(value ?? string.Empty));
        text.Append(last ? "\n" : ",\n");
    }

    /// <summary>
    ///     Quotes a JSON string by hand.
    /// </summary>
    /// <remarks>
    ///     No serializer, for the same reason the reader on the other side is hand-written: this file is
    ///     read inside Revit, where <c>System.Text.Json</c> is an assembly nobody pins and every second
    ///     vendor carries. Six escapes cover everything a button's text can hold.
    /// </remarks>
    private static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);

        text.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (character < ' ')
                        text.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        text.Append(character);
                    break;
            }
        }

        text.Append('"');
        return text.ToString();
    }
}
