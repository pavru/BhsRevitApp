using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// One registered pane: what Revit asks at setup, what it asks for content, and what the content is
/// given.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strings until the pane is seen.</b> Setup is answered from the manifest alone, and
/// <see cref="CreateFrameworkElement"/> hands back a plain WPF slot. The content class is resolved, and its
/// assembly loaded, only when Revit first shows the pane - so a pane nobody opens costs nothing but its
/// registration, and <c>Wpf.Ui</c> stays out of the AppDomain with it. Asking for the element is not
/// showing it: measured on 2026, Revit asks while it opens a model, with the pane never shown - and the
/// slot is visible to WPF at once all the same. Shown is what Revit's frame-visibility event says.
/// </para>
/// <para>
/// <b>Revit may ask for the element more than once, and gets one shell every time.</b> The creator's
/// documentation allows "creating each time"; how often Revit really calls it is not measured. Rebuilding
/// the content would lose the scroll, the selection and the focus a pane is kept open for, and handing the
/// same element back while it still has a parent throws. So each call gets a fresh, empty
/// <see cref="Decorator"/>, and the one shell moves into it.
/// </para>
/// <para>
/// <b>Nothing here throws into Revit.</b> What an exception out of <c>CreateFrameworkElement</c> does is
/// not known and not worth learning on somebody's machine. A content that cannot be found, built or cast
/// is a pane that says it stopped, why, and where the log is.
/// </para>
/// </remarks>
internal sealed class PaneSlot : IDockablePaneProvider, IFrameworkElementCreator, IPaneContext
{
    private readonly FeaturePane _pane;
    private readonly string _directory;
    private readonly Assembly _anchor;
    private readonly PaneDocuments _documents;
    private readonly ILog _log;

    private PaneShell? _shell;
    private Decorator? _latest;
    private bool _seen;
    private IPaneContent? _content;

    public PaneSlot(
        FeaturePane pane,
        RegisteredPane registered,
        string directory,
        Assembly anchor,
        IUiFeatureServices services,
        PaneDocuments documents,
        ILog log)
    {
        _pane = pane;
        Registered = registered;
        _directory = directory;
        _anchor = anchor;
        Services = services;
        _documents = documents;
        _log = log;
    }

    public RegisteredPane Registered { get; }

    public IUiFeatureServices Services { get; }

    public PaneDocument? Document => _documents.Descriptor;

    public PaneSelection Selection => _documents.Selection;

    public event EventHandler<PaneSelection>? SelectionChanged;

    public CultureInfo? Culture => RevitLanguage.Current;

    /// <summary>Called by Revit to learn how the pane starts. When, and on which thread, is not measured.</summary>
    public void SetupDockablePane(DockablePaneProviderData data)
    {
        var call = Registered.RecordSetup();

        try
        {
            // Only the creator, never FrameworkElement: an element set here would have to be built here,
            // at startup, with the feature assembly and Wpf.Ui loaded for a pane nobody opened.
            data.FrameworkElementCreator = this;
            data.VisibleByDefault = _pane.VisibleByDefault;
            data.EditorInteraction = new EditorInteraction(
                string.Equals(_pane.EditorInteraction, "KeepAlive", StringComparison.OrdinalIgnoreCase)
                    ? EditorInteractionType.KeepAlive
                    : EditorInteractionType.Dismiss);

            var state = new DockablePaneState
            {
                DockPosition = Position(_pane.DockPosition),
                MinimumWidth = _pane.MinimumWidth > 0 ? _pane.MinimumWidth : FeaturePane.DefaultMinimumWidth,
            };

            if (_pane.MinimumHeight > 0)
                state.MinimumHeight = _pane.MinimumHeight;

            data.InitialState = state;

            _log.Info("panes: {0} set up (call {1}, thread {2})", _pane.Name, call, Environment.CurrentManagedThreadId);
        }
        catch (Exception error)
        {
            _log.Error(error, "panes: {0} could not be set up", _pane.Name);
        }
    }

    /// <summary>Called by Revit when it needs the pane's element. The feature assembly loads here.</summary>
    public FrameworkElement CreateFrameworkElement()
    {
        var call = Registered.RecordCreate();
        _log.Info("panes: Revit asked for {0} (call {1}, thread {2})", _pane.Name, call, Environment.CurrentManagedThreadId);

        // Plain WPF only, until Revit says the pane is shown. Measured on 2026: Revit asks for the element
        // while it opens a model, with the pane never shown, so building here would load the content
        // assembly and Wpf.Ui for every session - the laziness the manifest exists for. WPF's own
        // IsVisible is no signal either: measured on the same run, the slot turned visible at once, with
        // IsShown false. What decides is DockableFrameVisibilityChanged, which the host forwards to
        // OnFrameShown; whichever of the two comes second places the shell.
        var slot = new Decorator();
        _latest = slot;

        if (_shell is not null || _seen)
            Place(slot);

        return slot;
    }

    /// <summary>Revit showed or hid this pane's frame. On the API thread.</summary>
    /// <remarks>
    /// By its documentation the event is raised when the frame "is just about to be shown or hidden", and
    /// it is not known yet whether the creator has run by then - so the shown state is remembered, and the
    /// creator places the shell itself when it comes second.
    /// </remarks>
    public void OnFrameShown(bool shown)
    {
        _log.Info("panes: {0} {1}", _pane.Name, shown ? "shown" : "hidden");

        if (!shown || _seen)
            return;

        _seen = true;

        if (_latest is { } slot)
            Place(slot);
    }

    private void Place(Decorator slot)
    {
        try
        {
            var shell = _shell ??= Build();

            if (shell.Parent is Decorator previous)
                previous.Child = null;

            slot.Child = shell;
        }
        catch (Exception error)
        {
            // The shell itself could not be built - WPF-UI missing from the folder, say. The last resort
            // is plain WPF, which Revit is made of.
            _log.Error(error, "panes: {0} could not be built at all", _pane.Name);
            slot.Child = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        }
    }

    public IDisposable Busy(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            throw new ArgumentException("A waiting state says what it waits for.", nameof(caption));

        var shell = _shell;

        if (shell is null)
            return Nothing.Instance;

        if (shell.Dispatcher.CheckAccess())
            return shell.Busy(caption);

        return shell.Dispatcher.Invoke(() => shell.Busy(caption));
    }

    public async Task<T?> ReadAsync<T>(string name, Func<Document, T> read, CancellationToken cancellationToken = default)
    {
        // "Waiting for Revit" until the pump picks the work up, then whatever the feature's own caption
        // says - the Designer's rule: the wait for Revit's idle is the one a feature cannot describe.
        var waiting = Busy(ShellStrings.Get(ShellStrings.WaitingForRevit));

        try
        {
            return await Services.Pump.PostAsync<T?>(name, _ =>
            {
                waiting.Dispose();

                var document = _documents.Current;
                return document is null ? default : read(document);
            }, cancellationToken);
        }
        finally
        {
            waiting.Dispose();
        }
    }

    /// <summary>The current document changed. On the API thread.</summary>
    public void OnDocumentChanged(PaneDocument? document)
    {
        var shell = _shell;

        if (shell is null)
            return;

        OnShellThread(shell, () =>
        {
            shell.SetDocument(document is not null);

            try
            {
                _content?.DocumentChanged(document);
            }
            catch (Exception error)
            {
                _log.Error(error, "panes: {0} failed on a document change", _pane.Name);
            }
        });
    }

    /// <summary>The selection in the current document changed, or the document did. On the API thread.</summary>
    /// <remarks>
    /// Counted whether or not the pane was ever shown - the count is what the probe reads in a sweep that
    /// shows nothing - and passed on only once there is a shell, because until then nobody has subscribed.
    /// </remarks>
    public void OnSelectionChanged(PaneSelection selection)
    {
        Registered.RecordSelection();

        var shell = _shell;

        if (shell is null)
            return;

        OnShellThread(shell, () =>
        {
            try
            {
                SelectionChanged?.Invoke(this, selection);
            }
            catch (Exception error)
            {
                _log.Error(error, "panes: {0} failed on a selection change", _pane.Name);
            }
        });
    }

    /// <summary>Revit's theme changed, or may have. On the API thread.</summary>
    public void FollowTheme(bool dark)
    {
        var shell = _shell;

        if (shell is null)
            return;

        OnShellThread(shell, () =>
        {
            if (shell.AppliedDark == dark)
                return;

            shell.ApplyTheme(dark);
            _log.Info("panes: {0} follows Revit to the {1} theme", _pane.Name, dark ? "dark" : "light");
        });
    }

    /// <summary>Whether Revit has shown this pane's frame at least once this session.</summary>
    public bool Seen => _seen;

    /// <summary>At shutdown: lets the content let go of what it holds.</summary>
    public void Dispose()
    {
        try
        {
            (_content as IDisposable)?.Dispose();
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: {0} did not dispose cleanly", _pane.Name);
        }
    }

    private PaneShell Build()
    {
        var shell = new PaneShell();
        _shell = shell;

        shell.ApplyTheme(PlaceholderIcon.Dark);
        shell.SetDocument(_documents.Descriptor is not null);

        try
        {
            var type = PaneContentLoader.Resolve(_directory, _pane.ContentAssembly, _pane.ContentClassName, _anchor);

            if (Activator.CreateInstance(type) is not IPaneContent content)
            {
                throw new InvalidCastException(
                    $"{type.FullName} does not implement {typeof(IPaneContent).FullName} as this host knows it " +
                    "- either it does not implement it at all, or it was loaded against another copy of " +
                    "BHS.Revit.Abstractions");
            }

            var element = content.Create(this);
            shell.SetContent(element);
            _content = content;

            _log.Info("panes: {0} content {1} created from {2}", _pane.Name, type.FullName, type.Assembly.Location);

            // Told the document it opens on, as it will be told every change after.
            content.DocumentChanged(_documents.Descriptor);
        }
        catch (Exception error)
        {
            var reason = (error as TargetInvocationException)?.InnerException?.Message ?? error.Message;

            _log.Error(error, "panes: {0} stopped - its content {1} from {2} could not be created",
                _pane.Name, _pane.ContentClassName, _pane.ContentAssembly);

            shell.ShowError(reason, LogPath());
        }

        // A pane created while a model was already open, in a session we joined late, has seen no
        // activation: ask once, through the pump, which is where UIApplication is.
        if (_documents.Descriptor is null)
            Services.Pump.Post("panes: seed the current document", session => _documents.Seed(session.Application));

        return shell;
    }

    private static string LogPath() =>
        LogRouter.Default.Sinks.OfType<FileLogSink>().FirstOrDefault()?.Path is { Length: > 0 } path
            ? path
            : FileLogSink.DefaultDirectory;

    private static DockPosition Position(string value) => value.ToLowerInvariant() switch
    {
        "left" => DockPosition.Left,
        "top" => DockPosition.Top,
        "bottom" => DockPosition.Bottom,
        _ => DockPosition.Right,
    };

    private static void OnShellThread(PaneShell shell, Action action)
    {
        if (shell.Dispatcher.CheckAccess())
            action();
        else
            shell.Dispatcher.BeginInvoke(action);
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
