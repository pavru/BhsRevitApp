using Autodesk.Revit.ApplicationServices;
using BHS.Shared;

namespace BHS.Revit.Abstractions;

/// <summary>
/// What a feature is handed to reach the running Revit session.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>UIApplication</c> here, and its absence is the design.</b> The first version of
/// this interface had one, and it could not be built: <c>OnStartup</c> is handed a
/// <c>UIControlledApplication</c>, and that type has no member returning a <c>UIApplication</c> on
/// any of the four supported releases - checked against the metadata rather than remembered.
/// </para>
/// <para>
/// The deeper reason is better than the mechanical one. Every place a <c>UIApplication</c> is
/// legitimately available gets it from Revit itself, as a parameter: a command from
/// <c>commandData.Application</c>, an event handler from <c>IExternalEventHandler.Execute</c>. So
/// whoever asks a context for one does not really want it - they want to be on the API thread, and
/// that is what <see cref="IRevitApiPump"/> is for. A <c>UIApplication</c> handed out here would be
/// used from a channel call, which measurement says never arrives on the API thread.
/// </para>
/// </remarks>
public interface IRevitContext
{
    /// <summary>The release this assembly was built for.</summary>
    RevitRelease Release { get; }

    /// <summary>The thread Revit calls its API on. Every API call has to happen there.</summary>
    int ApiThreadId { get; }

    /// <summary>Which add-in this is, as Revit knows it. The key everything else is filed under.</summary>
    Guid AddInId { get; }

    /// <summary>What <c>OnStartup</c> was given, and all of Revit that exists that early.</summary>
    ControlledApplication Controlled { get; }

    /// <summary>
    /// Whether Revit has a session - a <c>UIApplication</c> - rather than only a starting process.
    /// </summary>
    /// <remarks>
    /// A real distinction, not a formality: measured, our registration reaches a companion about
    /// twice as early as a usable main window exists. A module that wants to act "when Revit is
    /// there" means this, not the end of <c>OnStartup</c>.
    /// </remarks>
    bool IsSessionReady { get; }

    /// <summary>Raised once, on the API thread, when the session appears.</summary>
    event EventHandler? SessionReady;
}
