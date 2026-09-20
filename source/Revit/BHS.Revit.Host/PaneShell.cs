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
/// cell. <b>All three states are opaque, and the content under them is hidden rather than collapsed</b>,
/// so it keeps its layout - scroll position included - for when the state goes away.
/// </para>
/// <para>
/// <b>The waiting state hides the content too, and that is the point.</b> Until 2026-09-20 it was the
/// odd one out: a transparent layer over a content dimmed to 0.4, which left the previous answer legible
/// underneath. The owner found what that means on a live 2026, by hand: selecting a circuit put the
/// waiting layer over <i>the answer about the element selected before it</i>, and a dimmed sentence about
/// another element reads as the answer about this one. That is a plain lie about the model, and no
/// feature can undo it from its side - the shell is what stands between an answer and the next question.
/// Fixing it in each feature instead would be a rule every future pane has to remember, and a forgotten
/// clear looks exactly like a slow read. The cost is named and not measured: a read that returns fast
/// flashes the waiting card. Every read here goes through the pump, so it waits for Revit's idle at
/// best - flashes are not expected to be the common case, and a timer that delays the card would trade
/// this defect back for a shorter one.
/// </para>
/// </remarks>
internal sealed class PaneShell : Border
{
    private const double Padding12 = 12;
    private const double RingSize = 24;

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

        // No document: over a hidden content.
        var noDocumentText = Paragraph(ShellStrings.Get(ShellStrings.NoDocument), "TextFillColorPrimaryBrush");
        _noDocument = Layer(noDocumentText);

        // Waiting: ring and caption side by side, the ring pinned to a box of its own size. A StackPanel
        // over the caption put the two at the mercy of what ProgressRing reports as its desired size; a
        // Border with an explicit Width and Height reports that size whatever its child does, so the auto
        // column cannot collapse under the ring, and the caption's left margin absorbs a ring that paints
        // a little outside itself. Whether WPF-UI's ring does paint outside itself is not measured.
        _busyCaption = Paragraph(string.Empty, "TextFillColorSecondaryBrush");
        _busyCaption.Margin = new Thickness(Padding12, 3, 0, 0);

        var ringBox = new Border
        {
            Width = RingSize,
            Height = RingSize,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ProgressRing { IsIndeterminate = true, Width = RingSize, Height = RingSize },
        };

        var busyRow = new Grid();
        busyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        busyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(ringBox, 0);
        Grid.SetColumn(_busyCaption, 1);
        busyRow.Children.Add(ringBox);
        busyRow.Children.Add(_busyCaption);
        _busy = Layer(busyRow);

        // Stopped: marked by a strip rather than by red text.
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
        _error = Layer(strip);

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

        // Hidden, not Collapsed: the content keeps its layout for when the state clears. Hidden while
        // waiting too - an answer about the previous question must not be readable under the wait for
        // the answer to this one.
        _content.Visibility = _failed || noDocument || busy ? Visibility.Hidden : Visibility.Visible;
    }

    private static System.Windows.Controls.TextBlock Paragraph(string text, string brush)
    {
        var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>One overlay: top-left, 24 from the top, 12 elsewhere.</summary>
    /// <remarks>
    /// The pane's own background, never null and never transparent: it hides the content beneath, and a
    /// background is what makes the layer hit-test visible, which is how the waiting layer keeps clicks
    /// off a content that cannot answer them.
    /// </remarks>
    private static Border Layer(UIElement child)
    {
        var layer = new Border
        {
            Padding = new Thickness(Padding12, 24, Padding12, Padding12),
            Child = child,
        };

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
