using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// The Win-side end, on the well-known name, for the length of one sweep.
/// </summary>
/// <remarks>
/// Registration and the register behind it belong to <see cref="WinSideService"/>; what is left
/// here is what a probe runner in particular answers. The sweep is the first real consumer of the
/// registry, which is the point of it living in the transport rather than here.
/// </remarks>
internal sealed class RunnerChannel : WinSideService
{
    public RunnerChannel(RevitInstanceRegistry registry)
        : base(registry, "probe-runner")
    {
    }

    public override Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();
        response.Values.Add("echo", request.Question);
        return Task.FromResult(response);
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context) =>
        Task.FromResult(new ShutdownResponse());
}
