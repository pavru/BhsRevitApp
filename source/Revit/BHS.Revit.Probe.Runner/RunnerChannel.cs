using System.Collections.Concurrent;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// What arrived when a Revit instance announced itself.
/// </summary>
internal sealed class Registration
{
    public Registration(RegisterRequest request, string? callerAccount, TimeSpan elapsed)
    {
        Request = request;
        CallerAccount = callerAccount;
        Elapsed = elapsed;
    }

    public RegisterRequest Request { get; }

    /// <summary>The account the call was made under, learned by impersonating the caller.</summary>
    public string? CallerAccount { get; }

    /// <summary>How long after the launch the registration arrived.</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>
/// The Win-side end, on the well-known name, for the length of one sweep.
/// </summary>
/// <remarks>
/// A registry keyed by correlation token rather than by process id. The measured fact is that
/// <c>Revit.exe</c> does host its own add-ins, so a process id would work; the token is what tells
/// several Revits apart when more than one is running, and it is also the only thing that
/// distinguishes a Revit this runner started from one a person opened while it was running.
/// </remarks>
internal sealed class RunnerChannel : WinSideChannel.WinSideChannelBase
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Registration>> _expected = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan> _elapsed;

    public RunnerChannel(Func<TimeSpan> elapsed) => _elapsed = elapsed;

    /// <summary>Registrations that arrived without a token this runner issued.</summary>
    /// <remarks>
    /// Not an error. A person's Revit registers too, and seeing it here is how the runner knows to
    /// leave it alone instead of driving somebody's session.
    /// </remarks>
    public ConcurrentBag<RegisterRequest> Unexpected { get; } = new();

    /// <summary>Announces that a token has been issued, before the process that carries it starts.</summary>
    public Task<Registration> Expect(string correlationToken)
    {
        var waiter = new TaskCompletionSource<Registration>(TaskCreationOptions.RunContinuationsAsynchronously);
        _expected[correlationToken] = waiter;
        return waiter.Task;
    }

    public void Forget(string correlationToken) => _expected.TryRemove(correlationToken, out _);

    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        // Layer three, before anything is recorded: a caller that will not name a contract is not
        // a companion, whatever else it manages to say.
        Handshake.EnsureCompatible(request.ContractVersion);

        var account = PeerIdentity.TryGetCallerUserName(context);

        if (!string.IsNullOrEmpty(request.CorrelationToken)
            && _expected.TryGetValue(request.CorrelationToken, out var waiter))
        {
            waiter.TrySetResult(new Registration(request, account, _elapsed()));
        }
        else
        {
            Unexpected.Add(request);
        }

        return Task.FromResult(new RegisterResponse
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = "probe-runner",
        });
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
