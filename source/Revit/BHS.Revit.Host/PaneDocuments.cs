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
/// The order these events arrive in on close, and whether a cancelled close reports <c>Cancelled</c>,
/// are not measured.
/// </para>
/// </remarks>
internal sealed class PaneDocuments
{
    private readonly ILog _log;
    private Document? _current;
    private Document? _closing;

    public PaneDocuments(ILog log) => _log = log;

    /// <summary>Raised on the API thread with the new description, null when there is none.</summary>
    public event Action<PaneDocument?>? Changed;

    /// <summary>The document itself, for the pump's use only. Null when there is none, or it went away.</summary>
    public Document? Current => _current is { IsValidObject: true } current ? current : null;

    /// <summary>What a pane is told.</summary>
    public PaneDocument? Descriptor { get; private set; }

    public void Attach(UIControlledApplication application)
    {
        application.ViewActivated += OnViewActivated;
        application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        application.ControlledApplication.DocumentClosed += OnDocumentClosed;
    }

    public void Detach(UIControlledApplication application)
    {
        application.ViewActivated -= OnViewActivated;
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
        if (_current is null)
            Set(application.ActiveUIDocument?.Document, "seeded from the active document");
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        try
        {
            _closing = null;
            Set(args.Document, "view activated");
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

            if (args.Status == RevitAPIEventStatus.Succeeded)
                Set(null, "the document closed");
        }
        catch (Exception error)
        {
            _log.Warn(error, "panes: could not follow a closed document");
        }
    }

    private void Set(Document? document, string why)
    {
        if (ReferenceEquals(document, _current) || (document is not null && document.Equals(_current)))
            return;

        _current = document;
        Descriptor = document is null ? null : new PaneDocument(document.Title, document.IsFamilyDocument);

        _log.Debug("panes: document is now {0} ({1})", Descriptor?.ToString() ?? "(none)", why);
        Changed?.Invoke(Descriptor);
    }
}
