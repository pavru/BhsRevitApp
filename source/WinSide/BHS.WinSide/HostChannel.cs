using System.Globalization;
using BHS.Revit.Launch;
using BHS.Shared;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.WinSide;

/// <summary>
/// What this host answers when somebody asks it something.
/// </summary>
/// <remarks>
/// Registration is not here: it belongs to <see cref="WinSideService"/> and goes straight into the
/// registry. What is here is the part a host owns - what it will do when asked, which for now is
/// list what it knows, start a Revit, and close one.
/// <para>
/// Answers are flat string pairs because that is what the contract carries, and the contract
/// carries that because <c>Microsoft.Extensions.Configuration</c> is itself a flat string store.
/// A richer shape would mean either a second serializer in the AppDomain Revit shares with every
/// other add-in, or a message per question.
/// </para>
/// </remarks>
internal sealed class HostChannel : WinSideService
{
    private readonly WinSideHost _host;

    public HostChannel(WinSideHost host, RevitInstanceRegistry registry, string instanceId)
        : base(registry, instanceId) =>
        _host = host;

    public override async Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();

        switch (request.Question)
        {
            case "ping":
                response.Values.Add("instance", InstanceId);
                response.Values.Add("pid", _host.ProcessId.ToString(CultureInfo.InvariantCulture));
                response.Values.Add("main", _host.IsMain ? "True" : "False");
                break;

            case "instances":
                Describe(response);
                break;

            case "launch":
                await LaunchAsync(request, response).ConfigureAwait(false);
                break;

            case "close":
                await CloseAsync(request, response).ConfigureAwait(false);
                break;

            case "recover":
                var found = await Registry.RecoverAsync().ConfigureAwait(false);
                response.Values.Add("recovered", found.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"No such question: '{request.Question}'."));
        }

        return response;
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context)
    {
        Console.WriteLine($"asked to stop: {request.Reason}");
        _host.RequestStop();
        return Task.FromResult(new ShutdownResponse());
    }

    private void Describe(AskResponse response)
    {
        var instances = Registry.Instances;
        response.Values.Add("count", instances.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var instance in instances)
        {
            response.Values.Add(
                "instance:" + instance.InstanceId,
                string.Join(" | ",
                    instance.Release.ToString(CultureInfo.InvariantCulture),
                    "pid " + instance.ProcessId.ToString(CultureInfo.InvariantCulture),
                    instance.PipeName,
                    instance.StartedByUs ? "ours" : "not ours",
                    instance.Verified ? "verified" : "UNVERIFIED",
                    instance.Origin.ToString()));

            // Only for what this host started, and only to the account that may connect at all.
            // The token is how a caller finds the instance it asked for; it is not a secret from
            // the user whose descriptor already guards this pipe.
            if (instance.CorrelationToken is { Length: > 0 } token)
                response.Values.Add("token:" + instance.InstanceId, token);
        }
    }

    /// <summary>
    /// Starts a Revit, and answers before it has finished starting.
    /// </summary>
    /// <remarks>
    /// A cold Revit start is around half a minute to registration and over a minute to a usable
    /// window. Blocking the call for that long would hold one of the four listener threads the
    /// library allows, and would make any caller with a user interface sit still. So the answer is
    /// the correlation token, and the caller watches <c>instances</c> for it to appear - which is
    /// the same thing this host does internally, and the reason the token exists at all.
    /// </remarks>
    private async Task LaunchAsync(AskRequest request, AskResponse response)
    {
        if (!request.Arguments.TryGetValue("release", out var wanted) || !RevitRelease.TryParse(wanted, out var release))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "launch needs a 'release' argument, such as 2026."));

        var installation = RevitInstallation.Find(release)
                           ?? throw new RpcException(new Status(StatusCode.NotFound, $"Revit {release} is not installed."));

        var blocked = AddInTrust.Untrusted(installation);
        if (blocked.Count > 0)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "Revit would stop and ask about: " + string.Join(", ", blocked.Select(one => one.ToString()))));
        }

        var options = new RevitLaunchOptions();

        if (request.Arguments.TryGetValue("model", out var model) && !string.IsNullOrEmpty(model))
            options.ModelPath = model;

        var token = await _host.BeginLaunchAsync(installation, options).ConfigureAwait(false);

        response.Values.Add("token", token);
        response.Values.Add("release", release.Year.ToString(CultureInfo.InvariantCulture));
    }

    private async Task CloseAsync(AskRequest request, AskResponse response)
    {
        if (!request.Arguments.TryGetValue("instance", out var instanceId) || string.IsNullOrEmpty(instanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "close needs an 'instance' argument."));

        var closed = await _host.CloseAsync(instanceId).ConfigureAwait(false);
        response.Values.Add("closed", closed ? "True" : "False");
    }
}
