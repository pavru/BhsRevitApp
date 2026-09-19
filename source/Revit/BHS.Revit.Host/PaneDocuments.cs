using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// Which document the host's panes are about, kept once for all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>ViewActivated</c> is the truth.</b> Its arguments carry the document of the view that became
/// active, and switching between two open models is a view activation - so this one event covers
/// opening, creating and switching. <c>DocumentClosing</c> is only a hint, because a close can be
/// cancelled; <c>DocumentClosed</c>, which carries nothing but an integer id, confirms it by its status.
/// When the last model closes no view activates, so the confirmed close is what empties the pane.
/// </para>
/// <para>
/// <b>Kept here, and never handed out.</b> The <c>Document</c> stays inside the host and is given to
/// feature code only inside the pump, through <c>IPaneContext.ReadAsync</c>; a pane is told a
/// <see cref="PaneDocument"/> - a description. Every handler here runs on the API thread and inside the
/// event Revit raised, so reading the title there is legitimate; nowhere else in a pane would it be.
/// </para>
/// <para>
/// <b>And what is selected in it, for the same reason and in the same place.</b>
/// <c>UIControlledApplication.SelectionChanged</c> is declared on all four supported releases - read from
/// the metadata of the 2024, 2025, 2026 and 2027 reference assemblies, with
/// <c>SelectionChangedEventArgs.GetSelectedElements</c> and <c>GetDocument</c> on each - so no release
/// needs the fallback of polling <c>Selection.GetElementIds</c> on idle. Its reference forbids a handler
/// to modify the document or change the selection; this one only reads the ids. A change of document
/// resets the selection to that document's own, read from the active view's <c>UIDocument</c> when the
/// sender of <c>ViewActivated</c> is a <c>UIApplication</c>, and to nothing otherwise - the next
/// selection event then corrects it. Whether the sender is one is not measured.
/// </para>
/// <para>
/// The order these events arrive in on close, whether a cancelled close reports <c>Cancelled</c>, and
/// whether a selection made through the API raises <c>SelectionChanged</c>, are not measured - the last
/// is what the probe's selection check asks.
/// </para>
/// </remarks>
internal sealed class PaneDocuments
{
    private readonly ILog _log;
    private Document? _current;
    private Document? _closing;
    private PaneSelection _selection = PaneSelection.Empty;

    public PaneDocuments(ILog log) => _log = log;

    /// <summary>
    /// Raised on the API thread with what is selected now - on every change of the selection, and on every
    /// change of document even when the ids are equal, since equal ids in another document are other elements.
    /// </summary>
    public event Action<PaneSelection>? SelectionChanged;

    /// <summary>What is selected in the current document, as last seen. Never null.</summary>
    public PaneSelection Selection => _selection;

    /// <summary>Raised on the API thread with the new description, null when there is none.</summary>
    public event Action<PaneDocument?>? Changed;

    /// <summary>The document itself, for the pump's use only. Null when there is none, or it went away.</summary>
    public Document? Current => _current is { IsValidObject: true } current ? current : null;

    /// <summary>What a pane is told.</summary>
    public PaneDocument? Descriptor { get; private set; }

    public void Attach(UIControlledApplication application)
    {
        application.ViewActivated += OnViewActivated;
        application.SelectionChanged += OnSelectionChanged;
        application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        application.ControlledApplication.DocumentClosed += OnDocumentClosed;
    }

    public void Detach(UIControlledApplication application)
    {
        application.ViewActivated -= OnViewActivated;
        application.SelectionChanged -= OnSelectionChanged;
        application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
    }

    /// <summary>
    /// Takes the active document when a pane is created and nothing has been seen yet - the add-in
    /// loaded into a running session, where the activation happened before we were listening.
    /// </summary>
    /// <remarks>Called from inside the pump, which is where <c>UIApplication</c> exists.</remarks>
    public void Seed(UIApplication application)
    {
        if (_current is not null)
            return;

        var view = application.ActiveUIDocument;

        if (Set(view?.Document, "seeded from the active document"))
            Select(SelectionOf(view), always: true);
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        try
        {
            _closing = null;

            if (Set(args.Document, "view activated"))
                Select(SelectionIn(sender as UIApplication, args.Document), always: true);
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: could not follow the activated view");
        }
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        try
        {
            if (args.Document is { } document && _current is not null && document.Equals(_current))
                _closing = document;
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: could not follow a closing document");
        }
    }

    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        try
        {
            if (_closing is null)
                return;

            _closing = null;

            if (args.Status == RevitAPIEventStatus.Succeeded && Set(null, "the document closed"))
                Select(PaneSelection.Empty, always: true);
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: could not follow a closed document");
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        try
        {
            // Only the pane's document: a selection event about any other has nothing to say to a pane.
            if (_current is null || args.GetDocument() is not { } document || !document.Equals(_current))
                return;

            Select(new PaneSelection(args.GetSelectedElements().Select(id => id.Value)), always: false);
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: could not follow a selection change");
        }
    }

    /// <summary>What the active view of <paramref name="application"/> has selected, when it shows <paramref name="document"/>.</summary>
    private static PaneSelection SelectionIn(UIApplication? application, Document? document)
    {
        var view = application?.ActiveUIDocument;

        return document is not null && view?.Document is { } active && active.Equals(document)
            ? SelectionOf(view)
            : PaneSelection.Empty;
    }

    private static PaneSelection SelectionOf(UIDocument? view) =>
        view is null ? PaneSelection.Empty : new PaneSelection(view.Selection.GetElementIds().Select(id => id.Value));

    private void Select(PaneSelection selection, bool always)
    {
        if (!always && selection.SameIds(_selection))
            return;

        _selection = selection;
        _log.Debug("panes: {0} element(s) selected", selection.Count);
        SelectionChanged?.Invoke(selection);
    }

    /// <returns>Whether the document changed.</returns>
    private bool Set(Document? document, string why)
    {
        if (ReferenceEquals(document, _current) || (document is not null && document.Equals(_current)))
            return false;

        _current = document;
        Descriptor = document is null ? null : new PaneDocument(document.Title, document.IsFamilyDocument);

        _log.Debug("panes: document is now {0} ({1})", Descriptor?.ToString() ?? "(none)", why);
        Changed?.Invoke(Descriptor);
        return true;
    }
}
