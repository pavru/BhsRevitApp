using Autodesk.Revit.UI;
using BHS.Logging;
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
    private readonly BHS.Revit.Abstractions.IFeatureServices _services;
    private readonly ExternalEvent _exit;
    private int _publishCount;

    public ProbeChannel(
        ProbeFacts facts,
        BHS.Settings.LayeredSettings? layers,
        BHS.Revit.Abstractions.IFeatureServices services,
        ExternalEvent exit)
    {
        _settings = new ProbeSettings(layers, services);
        _services = services;
        _facts = facts;
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

                case "ribbon":
                    response.Values.Add("ribbon:availabilityCalls", LocalAvailability.Calls.ToString());
                    response.Values.Add("ribbon:commandRuns", ProbeCommand.Runs.ToString());
                    break;

                case "model":
                    foreach (var pair in ModelSettings().GetAwaiter().GetResult())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "log":
                    foreach (var pair in DescribeLog())
                        response.Values.Add(pair.Key, pair.Value);
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

    /// <summary>
    /// What the logging layer came to inside this Revit.
    /// </summary>
    /// <remarks>
    /// Reported rather than asserted here, because the interesting answers are the ones nobody can
    /// predict from outside: which sinks actually attached, where the file ended up, and whether
    /// anything is being dropped. The runner turns them into checks.
    /// </remarks>
    private IReadOnlyDictionary<string, string> DescribeLog()
    {
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["log:file"] = ProbeLog.Path,
            ["log:dropped"] = LogRouter.Default.Dropped.ToString(),
            ["log:primaryThread"] = LogRouter.PrimaryThreadId.ToString(),
        };

        var index = 0;

        foreach (var sink in LogRouter.Default.Sinks)
        {
            report["log:sink:" + index.ToString("D2")] = sink.GetType().Name + " >= " + sink.Minimum;
            index++;
        }

        return report;
    }

    /// <summary>
    /// Writes a project setting into the open model and reads it back.
    /// </summary>
    /// <remarks>
    /// The only shape that compiles, and that is the point of it. A document reaches this code from
    /// exactly one place - the session handed to work running inside the pump - so "not on the API
    /// thread" cannot be written here, and "there is no document" has to be named rather than
    /// forgotten. Both failures used to be caught by checks; now they are unexpressible.
    /// <para>
    /// It exercises the whole path at once: the pump there and back, a transaction on the API
    /// thread, Extensible Storage written and read, and the project chain choosing the model's
    /// answer over the file's.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> ModelSettings()
    {
        const string key = "Model:Probe:Marker";

        var report = await _services.Pump.PostAsync("probe: model settings", session =>
        {
            var answer = new Dictionary<string, string>(StringComparer.Ordinal);
            var document = session.Application.ActiveUIDocument?.Document;

            if (document is null)
            {
                answer["model:document"] = "(none)";
                return answer;
            }

            answer["model:document"] = document.Title;
            answer["model:before"] = _services.ModelSettings.For(document)[key] ?? "(unset)";

            // Everything the check writes happens inside a group that is rolled back, so the
            // document ends as clean as it started.
            //
            // Not tidiness: a written document is a modified one, and Revit asks whether to save it
            // on the way out - a modal dialog in a sweep nobody is watching, which is the failure
            // this whole runner exists to avoid. A group rolls back transactions that have already
            // committed, so production Write is exercised unchanged rather than replaced by
            // something the sweep does differently from the product.
            using var group = new Autodesk.Revit.DB.TransactionGroup(document, "BHS probe: model settings");
            group.Start();

            _services.ModelSettings.Write(document, new Dictionary<string, string?>
            {
                [key] = "written-by-the-probe",
            });

            var after = _services.ModelSettings.For(document);
            answer["model:after"] = after[key] ?? "(unset)";
            answer["model:origin"] = after.Origin.ToString();

            // The other half of the rule: a key without the project prefix must not come from the
            // model at all, and must still answer from the ordinary chain.
            answer["model:userScoped"] = after["Probe:Marker"] ?? "(unset)";

            group.RollBack();
            answer["model:clean"] = document.IsModified ? "False" : "True";

            return answer;
        }).ConfigureAwait(false);

        return report;
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
