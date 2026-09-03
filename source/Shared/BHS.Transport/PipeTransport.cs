using Grpc.Core;
using GrpcDotNetNamedPipes;

namespace BHS.Transport;

/// <summary>
/// Creates the two ends of a channel. Thin on purpose: it exists so that the security defaults and
/// the timeouts are decided in one place rather than at every call site.
/// </summary>
/// <remarks>
/// The wire protocol is private. This keeps the gRPC programming model and the <c>.proto</c>
/// contracts, but the framing is the library's own rather than HTTP/2, so a stock gRPC client will
/// not connect and both ends have to be on this library. That is the price of working on Revit
/// 2024: there is no gRPC server for .NET Framework at all, and the .NET Framework client is
/// TLS-only, Windows 11 and later, without client or duplex streaming.
/// </remarks>
public static class PipeTransport
{
    /// <summary>
    /// How long a client waits for the server end to exist. Long enough to cover a companion that
    /// is starting, short enough to fail a call rather than hang it.
    /// </summary>
    /// <remarks>
    /// Not a budget for waiting on a Revit that is still coming up. A cold Revit start is minutes,
    /// and that wait belongs to the side that started the process, where the process handle can be
    /// watched: if it died, fail at once instead of at a timeout.
    /// </remarks>
    public static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A server on the local machine. Call <c>Start</c> on the result once the services are bound.
    /// </summary>
    /// <remarks>
    /// Each listener blocks a thread and the library's pool size is a hard constant of four, so a
    /// server costs four threads in the hosting process - worth knowing when the host is Revit.
    /// <para>
    /// Who may connect is the first of three layers of trust, and the only one settled here. The
    /// second is checking the process on the other end after connecting; the third is the version
    /// and identity handshake in the protocol. Layers two and three carry the weight on Revit
    /// 2024, where the framework's own defences - <c>FirstPipeInstance</c> and
    /// <c>CurrentUserOnly</c>, both .NET 6 and later - do not exist.
    /// </para>
    /// </remarks>
    public static NamedPipeServer CreateServer(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName))
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));

        return new NamedPipeServer(pipeName);
    }

    /// <summary>
    /// A call invoker addressed at a server on this machine, to be handed to a generated client.
    /// </summary>
    public static CallInvoker CreateClient(string pipeName, TimeSpan? connectionTimeout = null)
    {
        if (string.IsNullOrEmpty(pipeName))
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));

        var options = new NamedPipeChannelOptions
        {
            ConnectionTimeout = (int)(connectionTimeout ?? DefaultConnectionTimeout).TotalMilliseconds,
        };

        // "." is the local machine. The transport is deliberately local only: crossing a machine
        // boundary is a different problem with different security, and nothing needs it.
        return new NamedPipeChannel(".", pipeName, options);
    }
}
