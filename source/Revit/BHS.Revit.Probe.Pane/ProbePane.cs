using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BHS.Revit.Abstractions;
using BHS.Revit.Probe.Declaration;

namespace BHS.Revit.Probe.Pane;

/// <summary>
/// The probe pane's content: small on purpose, and writing down everything the sweep asks of the shell
/// around it.
/// </summary>
/// <remarks>
/// <para>
/// What a real feature writes and nothing more - a content and what it shows - so the shell is exercised
/// exactly as a feature exercises it. The measurements go to <see cref="PaneFacts"/>, in the declaration,
/// which the probe can read without loading this assembly.
/// </para>
/// <para>
/// <b>No WPF-UI type is named here</b>, although the shell merges WPF-UI's dictionaries above this
/// element. The theme questions are asked by name - which dictionary the shell merged, which brush a key
/// resolves to - so that this assembly does not decide whether Wpf.Ui loads; the shell does.
/// </para>
/// </remarks>
public sealed class ProbePane : IPaneContent
{
    private IPaneContext? _context;
    private TextBlock? _document;

    public FrameworkElement Create(IPaneContext context)
    {
        PaneFacts.RecordCreate();
        _context = context;

        var title = new TextBlock { Text = "BHS probe pane", TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        _document = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        _document.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(title);
        root.Children.Add(_document);

        PaneFacts.Inspect = () => Inspect(root);
        return root;
    }

    public void DocumentChanged(PaneDocument? document)
    {
        PaneFacts.RecordDocument(document?.Title);

        if (_document is not null)
            _document.Text = document?.ToString() ?? string.Empty;

        if (document is not null && PaneFacts.ReadTitle.Length == 0 && _context is not null)
            ReadTitle(_context);
    }

    /// <summary>The door a pane has to the model: the pump, handed the shell's current document.</summary>
    private static async void ReadTitle(IPaneContext context)
    {
        try
        {
            PaneFacts.RecordRead(await context.ReadAsync("probe pane: read the title", document => document.Title));
        }
        catch (Exception error)
        {
            PaneFacts.RecordRead("(failed: " + error.GetType().Name + ")");
        }
    }

    /// <summary>What the shell above this element carries. On the element's thread.</summary>
    private static IDictionary<string, string> Inspect(FrameworkElement element)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);

        // Up the tree to the first element whose own dictionary merges WPF-UI's theme dictionary: the
        // shell's root. Found by the dictionary's type name, so this assembly never names WPF-UI.
        DependencyObject? current = element;
        FrameworkElement? shell = null;

        while (current is not null)
        {
            if (current is FrameworkElement framework
                && framework.Resources.MergedDictionaries.Any(dictionary =>
                    dictionary.GetType().FullName == "Wpf.Ui.Markup.ThemesDictionary"))
            {
                shell = framework;
                break;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        var theme = shell?.Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.GetType().FullName == "Wpf.Ui.Markup.ThemesDictionary");

        facts["pane:themeSource"] = theme?.Source?.ToString() ?? "(none)";
        facts["pane:accentPrimary"] = Colour(element.TryFindResource("SystemAccentColorPrimaryBrush"));
        facts["pane:background"] = Colour(element.TryFindResource("BhsPaneBackgroundBrush"));
        facts["pane:shown"] = element.IsVisible ? "True" : "False";

        // What Revit paints behind the pane: the first ancestor above the shell with a solid background.
        // A note for the Designer, whose #F0F0F0 and #3B4453 are a reading of Revit's panels, not a sample.
        var above = shell is null ? null : VisualTreeHelper.GetParent(shell);
        var revit = "(none found)";

        while (above is not null)
        {
            var brush = above switch
            {
                Control control => control.Background,
                Panel panel => panel.Background,
                Border border => border.Background,
                _ => null,
            };

            if (brush is SolidColorBrush { Color.A: > 0 } solid)
            {
                revit = solid.Color.ToString(CultureInfo.InvariantCulture) + " on " + above.GetType().Name;
                break;
            }

            above = VisualTreeHelper.GetParent(above);
        }

        facts["pane:revitBackground"] = revit;
        return facts;
    }

    private static string Colour(object? resource) => resource switch
    {
        SolidColorBrush brush => brush.Color.ToString(CultureInfo.InvariantCulture),
        null => "(none)",
        _ => resource.GetType().Name,
    };
}
