using Autodesk.Revit.UI;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Revit.Probe;

/// <summary>
/// The Revit-side end of the channel, served from inside the Revit process.
/// </summary>
/// <remarks>
/// The same service the real Revit-side will serve, with an <c>Ask</c> that answers questions
/// about the process rather than about a model. Every method here runs on a pool thread of the
/// pipe server, never on Revit's API thread - which is the first thing the probe reports, because
/// it is the constraint every later feature has to be written around.
/// </remarks>
internal sealed class ProbeChannel : RevitSideChannel.RevitSideChannelBase
{
    private readonly ProbeFacts _facts;
    private readonly ProbeSettings _settings;
    private readonly ExternalEvent _exit;
    private int _publishCount;

    public ProbeChannel(ProbeFacts facts, ProbeSettings settings, ExternalEvent exit)
    {
        _facts = facts;
        _settings = settings;
        _exit = exit;
        Publisher = new ConfigurationPublisher(facts.InstanceId);
        Republish();
    }

    public ConfigurationPublisher Publisher { get; }

    /// <summary>Republishes everything this instance knows about itself.</summary>
    /// <remarks>
    /// Called at startup and again whenever a document opens, so a consumer sees the change
    /// through the ordinary reload path rather than through an event of its own.
    /// </remarks>
    public void Republish()
    {
        var values = _facts.Snapshot();
        values["Probe:Publications"] = (++_publishCount).ToString();
        Publisher.Publish(values);
    }

    public override Task<ConfigurationSnapshot> GetConfiguration(ConfigurationRequest request, ServerCallContext context) =>
        Task.FromResult(Publisher.Current);

    public override Task WatchConfiguration(
        ConfigurationRequest request,
        IServerStreamWriter<ConfigurationSnapshot> responseStream,
        ServerCallContext context) =>
        Publisher.WatchAsync(responseStream, context.CancellationToken);

    public override Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();

        try
        {
            switch (request.Question)
            {
                case "assemblies":
                    foreach (var pair in LoadedAssemblies.Report())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "context":
                    foreach (var pair in _facts.Context())
                        response.Values.Add(pair.Key, pair.Value);

                    // Measured here rather than asserted: the call arrives wherever the transport
                    // put it, and the API thread is whichever one OnStartup ran on.
                    response.Values.Add("thread:calling", Environment.CurrentManagedThreadId.ToString());
                    response.Values.Add("thread:api", _facts.ApiThreadId.ToString());
                    break;

                case "settings":
                    foreach (var pair in _settings.Report())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "publish":
                    Republish();
                    break;

                case "ping":
                    break;

                default:
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "The probe does not answer '" + request.Question + "'."));
            }
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception error)
        {
            ProbeLog.Write("ask '" + request.Question + "' failed", error);
            throw new RpcException(new Status(StatusCode.Internal, error.GetType().Name + ": " + error.Message));
        }

        response.Values["instance"] = _facts.InstanceId;
        response.Values["pid"] = _facts.ProcessId.ToString();
        response.Values["release"] = _facts.Release.ToString();
        response.Values["revision"] = Publisher.Current.Revision.ToString();
        response.Values["log"] = ProbeLog.Path;

        return Task.FromResult(response);
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context)
    {
        ProbeLog.Write("shutdown requested: " + request.Reason);

        // Raise and answer. Waiting for Revit to actually close would mean the caller never gets a
        // reply, since the reply travels over a pipe this process owns.
        _exit.Raise();

        return Task.FromResult(new ShutdownResponse());
    }
}
