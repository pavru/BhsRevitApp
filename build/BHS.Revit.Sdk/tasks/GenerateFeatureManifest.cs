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

            Write(buttons, panes);

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

    private void Write(ITaskItem[] buttons, ITaskItem[] panes)
    {
        var text = new StringBuilder();

        // Version 2 adds "panes" and a button's "pane". The host reads both; a host from before reading
        // this file ignores the keys it does not know, which is what a flat key -> string reader does.
        text.Append("{\n");
        text.Append("  // Generated by BHS.Revit.Sdk. Edit the RevitRibbonButton and RevitDockablePane items instead.\n");
        text.Append("  \"version\": \"2\",\n");
        text.Append("  \"assembly\": ").Append(Quote(AssemblyFileName)).Append(",\n");
        text.Append("  \"buttons\": [\n");

        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];

            text.Append("    {\n");
            Field(text, "name", button.ItemSpec, last: false);
            Field(text, "tab", button.GetMetadata("Tab"), last: false);
            Field(text, "panel", button.GetMetadata("Panel"), last: false);
            Field(text, "text", button.GetMetadata("Text"), last: false);
            Field(text, "toolTip", button.GetMetadata("ToolTip"), last: false);
            Field(text, "longDescription", button.GetMetadata("LongDescription"), last: false);
            Field(text, "availabilityClassName", button.GetMetadata("AvailabilityClassName"), last: false);
            Field(text, "order", button.GetMetadata("Order"), last: false);
            Field(text, "pane", button.GetMetadata("Pane"), last: false);
            Field(text, "className", button.GetMetadata("ClassName"), last: true);
            text.Append(index == buttons.Length - 1 ? "    }\n" : "    },\n");
        }

        text.Append("  ],\n");
        text.Append("  \"panes\": [\n");

        for (var index = 0; index < panes.Length; index++)
        {
            var pane = panes[index];

            text.Append("    {\n");
            Field(text, "name", pane.ItemSpec, last: false);
            // Normalised, so that the host and RefCheck compare one spelling of one id.
            Field(text, "id", Guid.Parse(pane.GetMetadata("Id")).ToString("D"), last: false);
            Field(text, "title", pane.GetMetadata("Title"), last: false);
            Field(text, "contentAssembly", pane.GetMetadata("ContentAssembly"), last: false);
            Field(text, "contentClassName", pane.GetMetadata("ContentClassName"), last: false);
            Field(text, "dockPosition", pane.GetMetadata("DockPosition"), last: false);
            Field(text, "minimumWidth", pane.GetMetadata("MinimumWidth"), last: false);
            Field(text, "minimumHeight", pane.GetMetadata("MinimumHeight"), last: false);
            Field(text, "editorInteraction", pane.GetMetadata("EditorInteraction"), last: false);
            Field(text, "visibleByDefault", pane.GetMetadata("VisibleByDefault"), last: true);
            text.Append(index == panes.Length - 1 ? "    }\n" : "    },\n");
        }

        text.Append("  ]\n");
        text.Append("}\n");

        var directory = Path.GetDirectoryName(OutputPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputPath, text.ToString(), new UTF8Encoding(false));
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
