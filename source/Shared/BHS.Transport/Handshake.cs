using Grpc.Core;

namespace BHS.Transport;

/// <summary>
/// The third of the three layers of trust: what the other end says about itself.
/// </summary>
/// <remarks>
/// The descriptor decides who may connect and <see cref="PeerIdentity"/> decides who did; this
/// decides whether the two ends understand the same protocol. It is the only layer that survives
/// a correct impostor - a process of the same user, at the right path, that simply speaks a
/// different version.
/// </remarks>
public static class Handshake
{
    /// <summary>
    /// The contract this build speaks. Bump it when a change would make an older peer misread a
    /// message rather than merely miss a field.
    /// </summary>
    /// <remarks>
    /// Protobuf takes care of fields appearing and disappearing, so this is not a message version.
    /// It is a version of the agreement around the messages - what the keys mean, which methods
    /// must exist, what an empty section stands for - and none of that is expressed in the .proto.
    /// </remarks>
    public const string ContractVersion = "1";

    /// <summary>
    /// Refuses a caller that speaks a different contract, with a status the caller can act on.
    /// </summary>
    /// <remarks>
    /// A refusal rather than a best effort: a peer on another contract will be wrong about
    /// something later, and later is harder to diagnose.
    /// </remarks>
    public static void EnsureCompatible(string? theirVersion)
    {
        if (string.IsNullOrEmpty(theirVersion))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "The caller did not state a contract version. This channel is not a general-purpose gRPC endpoint."));
        }

        if (!string.Equals(theirVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Contract version mismatch: this end speaks '{ContractVersion}', the caller speaks '{theirVersion}'."));
        }
    }
}

/// <summary>
/// Ties a Revit process that Win-side started to the registration that eventually arrives from it.
/// </summary>
/// <remarks>
/// Matching by process id would be the obvious thing and does not work: <c>Revit.exe</c> is around
/// 1.6 MB and behaves like a launcher, so the process id handed back by <c>Process.Start</c> need
/// not be the process that ends up hosting the add-in.
/// <para>
/// The token travels in an environment variable rather than on the command line, because a command
/// line is readable through WMI by any user on the machine, and a token that others can read is
/// not a token.
/// </para>
/// </remarks>
public static class CorrelationToken
{
    /// <summary>The variable a started Revit reads its token from.</summary>
    public const string EnvironmentVariable = "BHS_CORRELATION_TOKEN";

    /// <summary>A fresh token for one launch.</summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The token this process was started with, or null when it was started by a person rather
    /// than by Win-side. Not an error: most Revit sessions begin that way, and they register
    /// without a token.
    /// </summary>
    public static string? FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
