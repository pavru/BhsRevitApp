using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Grpc.Core;
using GrpcDotNetNamedPipes;

namespace BHS.Transport;

/// <summary>
/// The second of the three layers of trust: who is actually on the other end.
/// </summary>
/// <remarks>
/// The descriptor in <see cref="PipeAccess"/> says who may connect; this says who did. It is
/// needed because a name is not owned: nothing stops another process of the same user from
/// creating a pipe called <c>BHS.WinSide</c> first and answering in our place. On Revit 2024 that
/// cannot be prevented at all - <c>PipeOptions.FirstPipeInstance</c>, "create only if it does not
/// exist", is .NET 6 and later - so the name is claimed by whoever got there first and the only
/// remedy is to look at who that was.
/// <para>
/// Both directions are deliberately asymmetric, because that is what the platform offers. A client
/// can learn the server's process id; a server cannot learn the client's through this library,
/// which keeps the pipe handle to itself, and instead impersonates the caller to learn the
/// account. Neither is conclusive on its own, which is why there is a third layer in the protocol.
/// </para>
/// </remarks>
public static class PeerIdentity
{
    /// <summary>
    /// Asks the operating system which process is serving <paramref name="pipeName"/>, and where
    /// its image lives.
    /// </summary>
    /// <remarks>
    /// This has to open a connection of its own: the library owns the pipe handle behind a channel
    /// and does not hand it out, and a pipe's security descriptor cannot be read by path - .NET has
    /// no API for it, and even <c>Get-Acl</c> fails on a pipe the caller has just created. So the
    /// answer costs one short connection, made and dropped before the channel is built.
    /// <para>
    /// A false result means nobody is serving that name right now, which is also the answer to
    /// "is this companion alive". A true result is a fact about the moment it was asked: the
    /// process could exit and another take the name a moment later. Treat it as one of three
    /// layers, not as proof.
    /// </para>
    /// </remarks>
    public static bool TryGetServerProcess(string pipeName, TimeSpan timeout, out int processId, out string? imagePath)
    {
        processId = 0;
        imagePath = null;

        if (string.IsNullOrEmpty(pipeName))
            return false;

        try
        {
            using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.None);
            stream.Connect((int)timeout.TotalMilliseconds);

            if (!GetNamedPipeServerProcessId(stream.SafePipeHandle.DangerousGetHandle(), out var id))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            processId = (int)id;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The descriptor refused us. Someone else owns this name.
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }

        imagePath = TryGetImagePath(processId);
        return true;
    }

    /// <summary>
    /// The full path of a process's image, or null when it cannot be read - the process has
    /// already gone, or it belongs to another account.
    /// </summary>
    /// <remarks>
    /// Comparing this against the place the framework is installed is the point of knowing the
    /// process id at all. In a shipped build it should be joined by an Authenticode check: a path
    /// says where a file is, not who wrote it.
    /// </remarks>
    public static string? TryGetImagePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The account the current call was made under, seen from inside a server method, or null when
    /// the transport cannot tell.
    /// </summary>
    /// <remarks>
    /// Impersonation is the only route: the client's process id is behind the library's pipe
    /// handle. It answers "the same user as me?", which - given a descriptor that admits nobody
    /// else - is mostly a confirmation that the descriptor did its job.
    /// <para>
    /// Compare with how the ancestor solution authenticated callers: a regular expression over
    /// <c>ServerCallContext.Peer</c>, which for a loopback TCP channel admits every local process
    /// of every user on the machine.
    /// </para>
    /// </remarks>
    public static string? TryGetCallerUserName(ServerCallContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        // The context handed to a server method is the transport's own; there is no public way to
        // build one. A different implementation means a different transport, and this question has
        // no answer there.
        if (context is not NamedPipeCallContext pipeContext)
            return null;

        string? name = null;

        try
        {
            pipeContext.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                name = identity.Name;
            });
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        return name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
