using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using BHS.Revit.Abstractions;
using BHS.Shared;

namespace BHS.Revit.Host;

/// <summary>
/// The context handed to features, built once during <c>OnStartup</c>.
/// </summary>
/// <remarks>
/// It moved here from the common layer deliberately: the host constructs it, and a feature should
/// not be able to build itself a second one with different answers in it.
/// </remarks>
internal sealed class RevitContext : IRevitContext
{
    private int _sessionReady;

    public RevitContext(UIControlledApplication application, Guid addInId)
    {
        Controlled = application.ControlledApplication;
        AddInId = addInId;

        // Captured here because here is the only place it is true: OnStartup runs on the API
        // thread by definition, and nothing later can find out which one that was.
        ApiThreadId = Environment.CurrentManagedThreadId;
        Release = RevitRelease.Parse(Controlled.VersionNumber);
    }

    public RevitRelease Release { get; }

    public int ApiThreadId { get; }

    public Guid AddInId { get; }

    public ControlledApplication Controlled { get; }

    public bool IsSessionReady => System.Threading.Volatile.Read(ref _sessionReady) != 0;

    public event EventHandler? SessionReady;

    /// <summary>Called by the host when Revit has a session, on the API thread, once.</summary>
    internal void MarkSessionReady()
    {
        if (System.Threading.Interlocked.Exchange(ref _sessionReady, 1) != 0)
            return;

        SessionReady?.Invoke(this, EventArgs.Empty);
    }
}
