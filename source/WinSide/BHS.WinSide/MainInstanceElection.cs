using System.Security.Principal;
using System.Threading;

namespace BHS.WinSide;

/// <summary>
/// Decides which Win-side process gets the well-known name.
/// </summary>
/// <remarks>
/// A mutex rather than a race for the pipe name, because the race cannot be run: the library never
/// sets <c>PipeOptions.FirstPipeInstance</c> - it does not exist on .NET Framework at all - so
/// "create this name only if nobody has it" is unavailable, two servers on one name coexist in
/// silence, and connections land on either of them.
/// <para>
/// The name is machine-wide and keyed by user, and both halves of that are deliberate. Machine-wide
/// because pipe names are: the pipe namespace is not per-session, so two sessions would collide on
/// <c>BHS.WinSide</c> whatever a session-scoped mutex said. Keyed by user because the pipe's
/// descriptor is: it admits one account, so the right question is not "who owns this machine" but
/// "who owns this machine for this user". A single global name would elect one winner across all
/// sessions and leave every other user unable to connect to it.
/// </para>
/// <para>
/// Measured: a non-elevated process can create a <c>Global\</c> mutex on this machine, despite
/// <c>SeCreateGlobalPrivilege</c> not being present in its token. The privilege gates section
/// objects rather than mutexes.
/// </para>
/// </remarks>
public sealed class MainInstanceElection : IDisposable
{
    private readonly Mutex? _mutex;

    private MainInstanceElection(Mutex? mutex, bool won, string name)
    {
        _mutex = mutex;
        Won = won;
        Name = name;
    }

    /// <summary>True when this process is the one that should serve the well-known name.</summary>
    public bool Won { get; }

    /// <summary>The mutex name that was contested.</summary>
    public string Name { get; }

    /// <summary>
    /// Stands for election. Returns immediately, having won or lost.
    /// </summary>
    /// <remarks>
    /// Losing is not an error and not a reason to exit: an instance that lost still serves its own
    /// pid-suffixed name, which is what diagnostics and a second GUI connect to.
    /// </remarks>
    public static MainInstanceElection Hold()
    {
        var name = MutexName();

        try
        {
            // Not owned, only held. Ownership is thread-affine - it must be released from the
            // thread that took it, which an async host cannot promise - and none of it is needed:
            // what decides the election is whether the named object already existed, and keeping a
            // handle open is what keeps it existing.
            var mutex = new Mutex(initiallyOwned: false, name, out var won);
            return new MainInstanceElection(mutex, won, name);
        }
        catch (UnauthorizedAccessException)
        {
            // Somebody else's mutex of the same name, which we may not open. Treat it as a loss:
            // whatever holds it, it is not ours to take.
            return new MainInstanceElection(null, false, name);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return new MainInstanceElection(null, false, name);
        }
    }

    private static string MutexName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? identity.Name.Replace('\\', '.');

        return $@"Global\BHS.WinSide.{sid}";
    }

    public void Dispose() => _mutex?.Dispose();
}
