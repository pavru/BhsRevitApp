using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace BHS.Revit.Host;

/// <summary>
/// The frame every dockable pane of ours stands in: theme, background, and the three states no feature
/// should have to write - no document, waiting, stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class and <see cref="PaneAccent"/>'s callers are the only places in the host that name a
/// WPF-UI type, on purpose.</b> The JIT resolves a type when it compiles a method that uses it, so as
/// long as nothing on the startup path touches this class, <c>Wpf.Ui</c> stays out of Revit's AppDomain
/// until Revit first asks for a pane. The probe's normal mode checks exactly that.
/// </para>
/// <para>
/// <b>Theme dictionaries on our own root, never on Revit's.</b> <c>ApplicationThemeManager.Apply</c>
/// writes into <c>Application.Current.Resources</c> and repaints <c>Application.Current.MainWindow</c> -
/// inside Revit, Revit's own - read from the IL of 4.0.3. <c>ThemesDictionary</c> and
/// <c>ControlsDictionary</c> only set their <c>Source</c>, read from the same IL, so they are safe to
/// merge here. The theme follows Revit (<c>UIThemeManager.CurrentTheme</c>), never WPF-UI's own idea
/// of the system theme.
/// </para>
/// <para>
/// <b>Controls that must not appear in a pane</b>, for the same reason, all read from the IL:
/// <c>FluentWindow</c>, <c>TitleBar</c>, <c>ClientAreaBorder</c> and <c>NavigationView</c> reach the
/// theme manager; <c>FontIcon</c> - and so <c>SymbolIcon</c>, <c>InfoBar</c> and every <c>Icon=</c> -
/// reads <c>UiApplication.Current.Resources</c> in its constructor, which here is Revit's.
/// </para>
/// <para>
/// Layout, from the Designer: one grid, the content always in it, each state an overlay in the same
/// cell. The content is hidden rather than collapsed under an opaque state, so it keeps its layout -
/// scroll position included - for when the state goes away.
/// </para>
/// </remarks>
internal sealed class PaneShell : Border
{
    private const double Padding12 = 12;

    private readonly Border _content = new();
    private readonly Border _noDocument;
    private readonly Border _busy;
    private readonly System.Windows.Controls.TextBlock _busyCaption;
    private readonly Border _error;
    private readonly System.Windows.Controls.TextBlock _errorText;
    private readonly System.Windows.Controls.TextBox _errorLog;
    private readonly List<BusyToken> _captions = new();

    private bool _hasDocument;
    private bool _failed;
    private bool? _dark;

    public PaneShell()
    {
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(this, 12);
        SetResourceReference(BackgroundProperty, PaneAccent.BackgroundKey);

        // No document: opaque, over a hidden content.
        var noDocumentText = Paragraph(ShellStrings.Get(ShellStrings.NoDocument), "TextFillColorPrimaryBrush");
        _noDocument = Layer(noDocumentText, opaque: true);

        // Busy: transparent but hit-test visible, so it blocks input over a dimmed content.
        _busyCaption = Paragraph(string.Empty, "TextFillColorSecondaryBrush");
        _busyCaption.Margin = new Thickness(0, 8, 0, 0);

        var ring = new ProgressRing { IsIndeterminate = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        var busyStack = new StackPanel();
        busyStack.Children.Add(ring);
        busyStack.Children.Add(_busyCaption);
        _busy = Layer(busyStack, opaque: false);

        // Stopped: opaque, marked by a strip rather than by red text.
        _errorText = Paragraph(string.Empty, "TextFillColorPrimaryBrush");
        _errorLog = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, Padding12, 0, 0),
        };
        _errorLog.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");

        var errorStack = new StackPanel();
        errorStack.Children.Add(_errorText);
        errorStack.Children.Add(_errorLog);

        var strip = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(Padding12, 0, 0, 0),
            Child = errorStack,
        };
        strip.SetResourceReference(BorderBrushProperty, "SystemFillColorCriticalBrush");
        _error = Layer(strip, opaque: true);

        var grid = new Grid();
        grid.Children.Add(_content);
        grid.Children.Add(_noDocument);
        grid.Children.Add(_busy);
        grid.Children.Add(_error);
        Child = grid;

        Refresh();
    }

    /// <summary>The theme applied last: true for dark, null before the first.</summary>
    public bool? AppliedDark => _dark;

    /// <summary>What the feature built.</summary>
    public void SetContent(FrameworkElement content) => _content.Child = content;

    /// <summary>Whether there is a document for the pane to be about.</summary>
    public void SetDocument(bool present)
    {
        _hasDocument = present;
        Refresh();
    }

    /// <summary>The pane stopped, and says why and where to read more. Final for the session.</summary>
    public void ShowError(string reason, string logPath)
    {
        _failed = true;
        _errorText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            ShellStrings.Get(ShellStrings.Failed), reason, logPath);
        _errorLog.Text = logPath;
        Refresh();
    }

    /// <summary>Shows <paramref name="caption"/> until the result is disposed. UI thread only.</summary>
    public IDisposable Busy(string caption)
    {
        var token = new BusyToken(this, caption);
        _captions.Add(token);
        Refresh();
        return token;
    }

    /// <summary>
    /// Merges WPF-UI's dictionaries for <paramref name="dark"/> into our root and writes our accent over
    /// them. UI thread only.
    /// </summary>
    /// <remarks>
    /// On a change the theme dictionary is replaced, not re-pointed: a new instance in the merged
    /// collection is a change WPF propagates to every <c>DynamicResource</c> below; whether editing
    /// <c>Source</c> in place does the same was not worth finding out. Measured neither way - the probe's
    /// theme switch is the first time this runs against a live change.
    /// </remarks>
    public void ApplyTheme(bool dark)
    {
        if (_dark == dark)
            return;

        var merged = Resources.MergedDictionaries;
        var theme = new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light };

        if (merged.Count == 0)
        {
            merged.Add(theme);
            merged.Add(new ControlsDictionary());
        }
        else
        {
            merged[0] = theme;
        }

        // The root's own entries, after the merge, because own entries win over merged ones - which is
        // the whole mechanism by which our accent beats Accent.xaml's blue.
        foreach (var pair in PaneAccent.Resources(dark))
            Resources[pair.Key] = pair.Value;

        _dark = dark;
    }

    private void Remove(BusyToken token)
    {
        if (_captions.Remove(token))
            Refresh();
    }

    private void Refresh()
    {
        var busy = _captions.Count > 0 && !_failed;
        var noDocument = !_hasDocument && !_failed;

        _error.Visibility = _failed ? Visibility.Visible : Visibility.Collapsed;
        _noDocument.Visibility = noDocument ? Visibility.Visible : Visibility.Collapsed;
        _busy.Visibility = busy && !noDocument ? Visibility.Visible : Visibility.Collapsed;
        _busyCaption.Text = busy ? _captions[_captions.Count - 1].Caption : string.Empty;

        // Hidden, not Collapsed: the content keeps its layout for when the state clears.
        _content.Visibility = _failed || noDocument ? Visibility.Hidden : Visibility.Visible;
        _content.Opacity = busy ? 0.4 : 1.0;
    }

    private static System.Windows.Controls.TextBlock Paragraph(string text, string brush)
    {
        var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>One overlay: top-left, 24 from the top, 12 elsewhere.</summary>
    private static Border Layer(UIElement child, bool opaque)
    {
        var layer = new Border
        {
            Padding = new Thickness(Padding12, 24, Padding12, Padding12),
            Child = child,
            // Transparent rather than null: a null background is not hit-test visible, and the busy
            // layer exists to stop clicks reaching a content that is waiting.
            Background = Brushes.Transparent,
        };

        if (opaque)
            layer.SetResourceReference(BackgroundProperty, PaneAccent.BackgroundKey);

        return layer;
    }

    private sealed class BusyToken : IDisposable
    {
        private PaneShell? _shell;

        public BusyToken(PaneShell shell, string caption)
        {
            _shell = shell;
            Caption = caption;
        }

        public string Caption { get; }

        public void Dispose()
        {
            // Idempotent, and on the shell's own thread whoever disposes: the pump releases the
            // "waiting for Revit" caption from inside its own execution.
            var shell = Interlocked.Exchange(ref _shell, null);

            if (shell is null)
                return;

            if (shell.Dispatcher.CheckAccess())
                shell.Remove(this);
            else
                shell.Dispatcher.BeginInvoke(new Action(() => shell.Remove(this)));
        }
    }
}
