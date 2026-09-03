using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BHS.Transport;

/// <summary>
/// The first of the three layers of trust: who is allowed to connect at all.
/// </summary>
/// <remarks>
/// The other two are checking the process on the other end after connecting, and the version and
/// identity handshake in the protocol. Three layers rather than one because on Revit 2024 the
/// framework's own defences are missing: <c>PipeOptions.FirstPipeInstance</c> and
/// <c>CurrentUserOnly</c> both arrived in .NET 6, so "create this pipe only if it does not exist"
/// and "connect only to a server owned by me" are simply unavailable there. An explicit
/// descriptor, written the same way on every framework, is what replaces them.
/// <para>
/// It is also the only layer that can be settled before a connection exists. Telling our pipe from
/// an impostor's by owner is not possible from the outside: .NET cannot read a pipe's security
/// descriptor by path - <c>Get-Acl</c> fails even on a pipe the caller has just created - and
/// enumeration yields names and nothing else.
/// </para>
/// </remarks>
public static class PipeAccess
{
    /// <summary>
    /// A descriptor granting the account that is running this code, and nobody else.
    /// </summary>
    /// <remarks>
    /// Both sides run as the signed-in user - Revit needs an interactive session, and Win-side is a
    /// background application rather than a service - so the two ends share an account and nothing
    /// wider is needed. An administrator can still take ownership of anything; that is a property
    /// of the system, not a hole in this descriptor.
    /// </remarks>
    public static PipeSecurity CurrentUserOnly()
    {
        using var identity = WindowsIdentity.GetCurrent();

        var user = identity.User
                   ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

        var security = new PipeSecurity();

        // The owner matters as much as the rule: an owner can always rewrite the descriptor, so
        // leaving it unset would be an odd thing to do deliberately.
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }
}
