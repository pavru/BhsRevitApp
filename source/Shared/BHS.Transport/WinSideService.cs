using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Transport;

/// <summary>
/// The Win-side end of the channel: the part every host serves the same way.
/// </summary>
/// <remarks>
/// <c>Register</c> is framework business rather than a feature's, so it is sealed here and wired
/// to the registry. What a host still owns is <c>Ask</c> and <c>Shutdown</c>, which mean different
/// things to different hosts and are left to the base class to be overridden.
/// <para>
/// Derive, bind to a server on <see cref="PipeNames.WinSide"/>, and the registry fills itself.
/// </para>
/// </remarks>
public abstract class WinSideService : WinSideChannel.WinSideChannelBase
{
    protected WinSideService(RevitInstanceRegistry registry, string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));

        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        InstanceId = instanceId;
    }

    /// <summary>What this host answers with when asked who it is.</summary>
    public string InstanceId { get; }

    /// <summary>The register this service fills.</summary>
    protected RevitInstanceRegistry Registry { get; }

    /// <summary>
    /// A Revit process announcing itself.
    /// </summary>
    /// <remarks>
    /// All three layers of trust meet here, in the order they can be applied: the descriptor
    /// already decided who could connect, impersonation says which account did, and the contract
    /// version is checked before anything is recorded - a caller that will not name one is not a
    /// companion, whatever else it manages to say.
    /// </remarks>
    public sealed override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        Handshake.EnsureCompatible(request.ContractVersion);

        if (string.IsNullOrEmpty(request.InstanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A registration must name an instance."));

        if (string.IsNullOrEmpty(request.PipeName))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A registration must say where to call back."));

        var instance = Registry.Record(request, PeerIdentity.TryGetCallerUserName(context));
        OnRegistered(instance);

        return Task.FromResult(new RegisterResponse
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = InstanceId,
        });
    }

    /// <summary>Called after an instance has been recorded, on the registering call's thread.</summary>
    /// <remarks>
    /// For a host that wants to act on the arrival rather than merely know of it. The registry
    /// raises <see cref="RevitInstanceRegistry.Arrived"/> for recovered instances too; this fires
    /// only for the ones that announced themselves.
    /// </remarks>
    protected virtual void OnRegistered(RevitInstance instance)
    {
    }
}
