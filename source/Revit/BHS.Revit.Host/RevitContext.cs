using Autodesk.Revit.ApplicationServices;
using BHS.Revit.Abstractions;
using BHS.Shared;

namespace BHS.Revit.Host;

/// <summary>
/// The context handed to features, built once during <c>OnStartup</c>.
/// </summary>
/// <remarks>
/// It moved here from the common layer deliberately: the host constructs it, and a feature should
/// not be able to build itself a second one with different answers in it.
/// <para>
/// It takes a <c>ControlledApplication</c> and not the UI one on purpose: that is the whole of what
/// both host forms have. An <c>IExternalDBApplication</c> is handed exactly this and nothing else,
/// so a context built from it is the same context either way - including
/// <see cref="IsInitialized"/>, which was measured firing there as well.
/// </para>
/// </remarks>
internal sealed class RevitContext : IRevitContext
{
    private int _initialized;

    public RevitContext(ControlledApplication controlled, Guid addInId)
    {
        Controlled = controlled;
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

    public bool IsInitialized => System.Threading.Volatile.Read(ref _initialized) != 0;

    public event EventHandler? Initialized;

    /// <summary>Called by the host when Revit has finished starting, on the API thread, once.</summary>
    internal void MarkInitialized()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        Initialized?.Invoke(this, EventArgs.Empty);
    }
}
