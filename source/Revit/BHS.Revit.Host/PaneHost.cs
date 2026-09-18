using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// Registers the dockable panes the manifests declare, and keeps them following the document and the
/// theme.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered in <c>OnStartup</c>, because <c>UIControlledApplication</c> exists only there</b>, and
/// that object is passed into this call and never kept - the same rule as for the ribbon.
/// <c>RegisterDockablePane</c> is on it on all four releases (checked against the metadata of 2024 and
/// 2027); when Revit then calls setup - inside the registration or later, while its interface
/// initialises - is not measured, and <see cref="RegisteredPane.SetupDuringRegistration"/> records it.
/// </para>
/// <para>
/// <b>The same gate as the ribbon.</b> A pane in a feature's Entry manifest is registered only when the
/// edition's <c>Modules</c> list declares a module from <c>&lt;P&gt;.Declaration</c>; the ribbon has
/// already said so with an error when it is not, so this says it once more quietly. Its content is given
/// that module's services, narrowed the way the module itself was: its own settings section and log
/// category.
/// </para>
/// <para>
/// <b>Asked before registered.</b> <c>RegisterDockablePane</c> throws for an id already registered, and
/// on Revit 2024 two editions of ours share one AppDomain - so the host asks
/// <c>DockablePane.PaneIsRegistered</c> first and never competes for an id. A pane somebody else
/// registered is a line in the log and a pane of theirs, not ours.
/// </para>
/// </remarks>
internal sealed class PaneHost
{
    private readonly List<PaneSlot> _slots = new();
    private readonly ILog _log;
    private readonly PaneDocuments _documents;
    private bool _attached;

    public PaneHost(ILog log)
    {
        _log = log;
        _documents = new PaneDocuments(log);
        _documents.Changed += OnDocumentChanged;
    }

    /// <summary>How many panes this host registered with Revit.</summary>
    public int Count => _slots.Count;

    /// <returns>How many panes were registered.</returns>
    public int Register(
        UIControlledApplication application,
        IReadOnlyList<FeatureManifest> manifests,
        string directory,
        IReadOnlyList<IFeatureModule> modules,
        IUiFeatureServices services,
        Guid addInId,
        Assembly anchor)
    {
        foreach (var manifest in manifests)
        {
            if (manifest.Panes.Count == 0)
                continue;

            var fileName = Path.GetFileName(manifest.Path);
            var paneServices = services;

            if (manifest.IsEntry)
            {
                var declaration = manifest.EntryFeature + FeatureManifest.DeclarationSuffix;

                var module = modules.FirstOrDefault(candidate =>
                    candidate is not null
                    && string.Equals(candidate.GetType().Assembly.GetName().Name, declaration, StringComparison.OrdinalIgnoreCase));

                if (module is null)
                {
                    _log.Info("panes: the panes in {0} were not registered, for the reason the ribbon gave for its buttons",
                        fileName);
                    continue;
                }

                paneServices = services.For(module.GetType().Name).Ui();
            }

            var entryAssembly = Path.GetFileNameWithoutExtension(manifest.Assembly);

            foreach (var pane in manifest.Panes)
                Register(application, manifest, pane, directory, entryAssembly, paneServices, addInId, anchor);
        }

        if (_slots.Count > 0 && !_attached)
        {
            _documents.Attach(application);
            application.DockableFrameVisibilityChanged += OnFrameVisibilityChanged;
            _attached = true;
        }

        if (_slots.Count > 0)
            _log.Info("panes: {0} registered", _slots.Count);

        return _slots.Count;
    }

    /// <summary>Revit's theme changed, or may have: every live pane follows. On the API thread.</summary>
    public void FollowTheme()
    {
        if (_slots.Count == 0)
            return;

        var dark = PlaceholderIcon.Dark;

        foreach (var slot in _slots)
            slot.FollowTheme(dark);
    }

    public void Stop(UIControlledApplication application)
    {
        if (_attached)
        {
            try
            {
                application.DockableFrameVisibilityChanged -= OnFrameVisibilityChanged;
                _documents.Detach(application);
            }
            catch (Exception error)
            {
                _log.Warn(error, "panes: could not unsubscribe from the document events");
            }

            _attached = false;
        }

        foreach (var slot in _slots)
            slot.Dispose();
    }

    // The only signal that a pane is on the screen: the creator is called for panes nobody showed.
    private void OnFrameVisibilityChanged(object? sender, DockableFrameVisibilityChangedEventArgs args)
    {
        try
        {
            foreach (var slot in _slots)
            {
                if (args.PaneId == new DockablePaneId(slot.Registered.Id))
                    slot.OnFrameShown(args.DockableFrameShown);
            }
        }
        catch (Exception error)
        {
            _log.Error(error, "panes: a frame-visibility change could not be followed");
        }
    }

    private void Register(
        UIControlledApplication application,
        FeatureManifest manifest,
        FeaturePane pane,
        string directory,
        string entryAssembly,
        IUiFeatureServices services,
        Guid addInId,
        Assembly anchor)
    {
        try
        {
            if (pane.Id == Guid.Empty)
            {
                _log.Error("panes: {0} in {1} has no usable id, so it was not registered", pane.Name, manifest.Path);
                return;
            }

            // Checked now rather than when Revit asks: a missing assembly is a deployment mistake, and it is
            // better said at startup than discovered by a person opening the pane.
            if (!File.Exists(Path.Combine(directory, pane.ContentAssembly + ".dll")))
            {
                _log.Error("panes: {0} names its content in {1}.dll, which is not beside {2}, so it was not registered",
                    pane.Name, pane.ContentAssembly, manifest.Path);
                return;
            }

            var id = new DockablePaneId(pane.Id);

            if (DockablePane.PaneIsRegistered(id))
            {
                var ours = PaneRegistry.Find(pane.Id);

                _log.Warn(ours is null
                        ? "panes: {0} ({1}) is registered already, by an add-in that is not ours; this one was not"
                        : "panes: {0} ({1}) is registered already, by another host of ours ({2}); this one was not",
                    pane.Name, pane.Id, ours?.AddInId.ToString() ?? string.Empty);
                return;
            }

            var registered = new RegisteredPane(pane.Name, pane.Id, addInId);
            var slot = new PaneSlot(pane, registered, directory, anchor, services, _documents, _log);

            application.RegisterDockablePane(id, pane.Title, slot);

            registered.SetupDuringRegistration = registered.SetupCalls > 0;
            PaneRegistry.Register(registered);
            _slots.Add(slot);

            // Every button of this manifest that names the pane - one is the rule, more is allowed.
            foreach (var button in manifest.Buttons.Where(button =>
                         string.Equals(button.Pane, pane.Name, StringComparison.OrdinalIgnoreCase)))
            {
                PaneRegistry.RegisterButton(entryAssembly, button.ClassName, pane.Id);
            }

            _log.Info("panes: {0} ({1}) registered, set up during registration: {2}",
                pane.Name, pane.Id, registered.SetupDuringRegistration);
        }
        catch (Exception error)
        {
            // A pane that could not be registered is a missing pane, never a failed start.
            _log.Error(error, "panes: {0} could not be registered", pane.Name);
        }
    }

    private void OnDocumentChanged(PaneDocument? document)
    {
        foreach (var slot in _slots)
            slot.OnDocumentChanged(document);
    }
}
