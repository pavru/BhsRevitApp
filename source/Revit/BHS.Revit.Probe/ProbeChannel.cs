using System.Globalization;
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
    private readonly BHS.Revit.Abstractions.IUiFeatureServices _services;
    private readonly ExternalEvent _exit;
    private readonly ExternalEvent _press;
    private int _publishCount;

    public ProbeChannel(
        ProbeFacts facts,
        BHS.Settings.LayeredSettings? layers,
        BHS.Revit.Abstractions.IUiFeatureServices services,
        ExternalEvent exit,
        ExternalEvent press)
    {
        _press = press;
        _settings = new ProbeSettings(layers, services);
        _services = services;
        _facts = facts;
        _exit = exit;
        Publisher = new ConfigurationPublisher(facts.InstanceId);
        Republish();
    }

    public ConfigurationPublisher Publisher { get; }

    /// <summary>What Revit is doing, for a watcher that cannot see its screen.</summary>
    public DiagnosticsPublisher Diagnostics { get; } = new();

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

    /// <summary>
    /// Serves the diagnostic stream: the recent past, and then everything as it happens.
    /// </summary>
    /// <remarks>
    /// The handshake is checked here as everywhere else. A watcher that does not name the contract
    /// is refused rather than served a stream whose fields it may read wrongly - the same position
    /// taken in Register and in recovery by enumeration.
    /// </remarks>
    public override Task WatchDiagnostics(
        DiagnosticsRequest request,
        IServerStreamWriter<DiagnosticEvent> responseStream,
        ServerCallContext context)
    {
        Handshake.EnsureCompatible(request.ContractVersion);
        return Diagnostics.WatchAsync(responseStream, context.CancellationToken);
    }

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
                    response.Values.Add("ribbon:featureLoaded", ProbeApplication.IsFeatureLoaded() ? "True" : "False");
                    response.Values.Add("ribbon:fromManifest", ProbeApplication.ButtonsFromManifest.ToString());
                    response.Values.Add("ribbon:ownTab", ProbeApplication.OwnTabSeen ? "True" : "False");
                    response.Values.Add("ribbon:icons", ProbeApplication.IconsSeen ? "True" : "False");

                    foreach (var fact in ProbeApplication.IconFacts)
                        response.Values.Add(fact.Key, fact.Value);
                    response.Values.Add("ribbon:pingRuns",
                        Environment.GetEnvironmentVariable("BHS_PROBE_PING_RAN") ?? "0");
                    response.Values.Add("ribbon:pingAddInId",
                        Environment.GetEnvironmentVariable("BHS_PROBE_PING_SERVICES") ?? "(none)");
                    break;

                case "model":
                    foreach (var pair in ModelSettings().GetAwaiter().GetResult())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "survey":
                    foreach (var pair in SurveyCabling().GetAwaiter().GetResult())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "schema":
                    foreach (var pair in SchemaEvolution().GetAwaiter().GetResult())
                        response.Values.Add(pair.Key, pair.Value);
                    break;

                case "press":
                    _press.Raise();
                    break;

                // The other half of the add-in, which has no channel of its own on purpose: two
                // servers in one process would race for one pipe name, and the question here is
                // about the host, not the transport.
                case "dbhost":
                    response.Values.Add("db:started", ProbeDbApplication.Started ? "True" : "False");
                    response.Values.Add("db:addInId", ProbeDbApplication.AddInIdSeen);
                    response.Values.Add("db:initializedAtStart", ProbeDbApplication.InitializedAtStart ? "True" : "False");
                    response.Values.Add("db:settings", ProbeDbApplication.SettingsSeen);
                    response.Values.Add("db:uiRefusal", ProbeDbApplication.UiRefusal);
                    response.Values.Add("db:hostsRegistered", BHS.Revit.Abstractions.HostRegistry.Count.ToString());
                    response.Values.Add("db:apiThread", ProbeDbApplication.ApiThreadId.ToString());
                    response.Values.Add("db:uiApiThread", _services.Revit.ApiThreadId.ToString());
                    response.Values.Add("db:moduleStarted", ProbeDbModule.Started ? "True" : "False");
                    response.Values.Add("db:moduleSection", ProbeDbModule.SectionSeen);

                    // Both looked up by id, and asked whether they are the same object. "At least
                    // two are registered" would also pass if one host had registered twice.
                    var ui = BHS.Revit.Abstractions.HostRegistry.Find(ProbeApplication.Id);
                    var db = BHS.Revit.Abstractions.HostRegistry.Find(ProbeDbApplication.Id);

                    response.Values.Add("db:foundBoth", ui is not null && db is not null ? "True" : "False");
                    response.Values.Add("db:distinct", ui is not null && db is not null && !ReferenceEquals(ui, db) ? "True" : "False");
                    response.Values.Add("db:uiHasPump", ui is BHS.Revit.Abstractions.IUiFeatureServices ? "True" : "False");
                    response.Values.Add("db:dbHasPump", db is BHS.Revit.Abstractions.IUiFeatureServices ? "True" : "False");
                    response.Values.Add("db:initialized", ProbeDbApplication.Initialized ? "True" : "False");
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
    /// What a real project actually holds, before any code is written against a guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The routing core was designed from a predecessor's source and a conversation. This asks the
    /// model instead: how many carriers, of which categories, in the host or in links, how many
    /// circuits, and whether the two live in the same file at all - which is the question that
    /// decides whether <c>CarrierId</c>'s link half is exercised or theoretical.
    /// </para>
    /// <para>
    /// <b>Counts and category names only, and that is a rule rather than an oversight.</b> These are
    /// somebody's real project files, and this repository is public: family names, level names,
    /// circuit numbers and panel names would all identify the building and its author. A count
    /// cannot.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> SurveyCabling()
    {
        var report = await _services.Pump.PostAsync("probe: cabling survey", session =>
        {
            var answer = new Dictionary<string, string>(StringComparer.Ordinal);
            var document = session.Application.ActiveUIDocument?.Document;

            if (document is null)
            {
                answer["survey:document"] = "(none)";
                return answer;
            }

            var carriers = new[]
            {
                Autodesk.Revit.DB.BuiltInCategory.OST_CableTray,
                Autodesk.Revit.DB.BuiltInCategory.OST_CableTrayFitting,
                Autodesk.Revit.DB.BuiltInCategory.OST_Conduit,
                Autodesk.Revit.DB.BuiltInCategory.OST_ConduitFitting,
            };

            void CountIn(Autodesk.Revit.DB.Document where, string prefix)
            {
                foreach (var category in carriers)
                {
                    var found = new Autodesk.Revit.DB.FilteredElementCollector(where)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .GetElementCount();

                    if (found > 0)
                        answer[prefix + ":" + category] = found.ToString(CultureInfo.InvariantCulture);
                }

                var circuits = new Autodesk.Revit.DB.FilteredElementCollector(where)
                    .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_ElectricalCircuit)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                if (circuits > 0)
                    answer[prefix + ":circuits"] = circuits.ToString(CultureInfo.InvariantCulture);
            }

            CountIn(document, "host");

            // The question the whole link half of CarrierId rests on: are the carriers in this file
            // or in another one? Answered by looking rather than by assuming either way.
            var links = new Autodesk.Revit.DB.FilteredElementCollector(document)
                .OfClass(typeof(Autodesk.Revit.DB.RevitLinkInstance))
                .Cast<Autodesk.Revit.DB.RevitLinkInstance>()
                .ToList();

            answer["survey:links"] = links.Count.ToString(CultureInfo.InvariantCulture);

            var index = 0;

            foreach (var link in links)
            {
                index++;
                var linked = link.GetLinkDocument();

                if (linked is null)
                {
                    answer[$"link{index}:state"] = "not loaded";
                    continue;
                }

                // Whether a link can be written to at all - inferred so far from the shape of the
                // API and never attempted. Reported as what Revit says about the document, which is
                // as close as looking gets; the proof is a transaction, and that belongs in the
                // command rather than in a survey.
                answer[$"link{index}:linked"] = linked.IsLinked ? "True" : "False";
                answer[$"link{index}:readOnly"] = linked.IsReadOnly ? "True" : "False";
                answer[$"link{index}:modifiable"] = linked.IsModifiable ? "True" : "False";

                CountIn(linked, "link" + index.ToString(CultureInfo.InvariantCulture));
            }

            // One circuit, examined for the fields the snapshot needs. Nothing that names it.
            var sample = new Autodesk.Revit.DB.FilteredElementCollector(document)
                .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_ElectricalCircuit)
                .WhereElementIsNotElementType()
                .Cast<Autodesk.Revit.DB.Electrical.ElectricalSystem>()
                .FirstOrDefault();

            if (sample is not null)
            {
                answer["sample:pathMode"] = sample.CircuitPathMode.ToString();
                answer["sample:hasCustomPath"] = sample.HasCustomCircuitPath ? "True" : "False";
                answer["sample:pathPoints"] = sample.GetCircuitPath().Count.ToString(CultureInfo.InvariantCulture);
                answer["sample:elements"] = sample.Elements.Size.ToString(CultureInfo.InvariantCulture);
                answer["sample:hasPanel"] = sample.BaseEquipment is not null ? "True" : "False";
                answer["sample:length"] = sample.Length.ToString("F2", CultureInfo.InvariantCulture);
            }

            return answer;
        }).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// How permanent a schema really is, once a model carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CLAUDE.md</c> says a schema that has reached somebody else's model can never be changed,
    /// and the whole three-field shape of <c>BHS.ModelSettings</c> rests on that sentence. It is
    /// true of one GUID and says nothing about the price: a schema under a new GUID is a different
    /// schema, and a reader that tries the new one and falls back to the old migrates on the next
    /// write. So the question is not whether the window closes but what it costs to reopen it - and
    /// that is measurable rather than arguable.
    /// </para>
    /// <para>
    /// <b>Reported as measurements, not as checks, and deliberately.</b> Nobody here knows the
    /// answers yet, and a check written before its answer is a check that agrees with whoever wrote
    /// it. They become assertions in the commit that reads them - the same order that turned
    /// <c>IsSessionReady</c> into <c>IsInitialized</c>.
    /// </para>
    /// <para>
    /// <b>A throwaway GUID throughout.</b> Registration is process-wide and lasts the session, so
    /// this must never touch the identity the product writes; and the entity goes into the document
    /// inside a group that is rolled back, for the same reason the settings check does it - a
    /// modified document asks to be saved, and a sweep has nobody to answer.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> SchemaEvolution()
    {
        // Fixed rather than random, so two sessions are comparable; and nothing but this
        // measurement ever names it.
        var probeSchemaId = new Guid("3f4c8e21-7b9a-4d16-8c05-2a6e91d4b7f3");

        var report = await _services.Pump.PostAsync("probe: schema evolution", session =>
        {
            var answer = new Dictionary<string, string>(StringComparer.Ordinal);
            var document = session.Application.ActiveUIDocument?.Document;

            if (document is null)
            {
                answer["schema:document"] = "(none)";
                return answer;
            }

            Autodesk.Revit.DB.ExtensibleStorage.Schema Build(Guid id, bool extraField)
            {
                using var builder = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(id);
                builder.SetSchemaName("BHSProbeSchema");
                builder.SetVendorId("BimHouseSoftware");
                builder.SetReadAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Public);
                builder.SetWriteAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Vendor);
                builder.AddSimpleField("Version", typeof(string));
                builder.AddMapField("Values", typeof(string), typeof(string));
                builder.AddArrayField("Cleared", typeof(string));

                if (extraField)
                    builder.AddSimpleField("Extra", typeof(string));

                return builder.Finish();
            }

            string Attempt(Action work)
            {
                try
                {
                    work();
                    return "ok";
                }
                catch (Exception error)
                {
                    return error.GetType().Name + ": " + error.Message.Split('\n')[0];
                }
            }

            // 1. The shape the product uses today, under a name only this measurement knows.
            //
            // Guarded like every other step, and the reason is written down elsewhere in this
            // repository at the price of a whole release: an exception escaping here leaves Ask to
            // rethrow it, and the runner has no try around its checks - so the rest of the release
            // is lost and Revit is never asked to close. The measurement reports its own failure
            // as a value; it does not take the sweep with it.
            answer["schema:registered"] = Attempt(() =>
            {
                var first = Build(probeSchemaId, extraField: false);
                answer["schema:fields"] = string.Join(",", first.ListFields().Select(f => f.FieldName));
            });

            // 2. The same definition again. If this alone throws, every later question is moot:
            //    the answer would be that a definition may be built exactly once per process.
            answer["schema:sameAgain"] = Attempt(() => Build(probeSchemaId, extraField: false));

            // 3. The question the whole shape rests on: a fourth field under the same identity.
            answer["schema:extraField"] = Attempt(() => Build(probeSchemaId, extraField: true));

            // 4. Whichever way that went, what does the registry hold now? A definition silently
            //    replaced would be worse than one refused.
            var current = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(probeSchemaId);
            answer["schema:fieldsAfter"] = current is null
                ? "(gone)"
                : string.Join(",", current.ListFields().Select(f => f.FieldName));

            if (answer["schema:registered"] != "ok")
                return answer;

            using var group = new Autodesk.Revit.DB.TransactionGroup(document, "BHS probe: schema");
            answer["schema:group"] = Attempt(() => group.Start());

            // 5. An entity written under the definition as it stands, read back, and asked about a
            //    field it may or may not know. RecognizedField reads like the tolerance mechanism;
            //    whether it is one is exactly what is not known.
            answer["schema:roundTrip"] = Attempt(() =>
            {
                var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(probeSchemaId)!;
                var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);
                entity.Set("Version", "1");
                entity.Set<IDictionary<string, string>>("Values",
                    new Dictionary<string, string> { ["k"] = "v" });
                entity.Set<IList<string>>("Cleared", new List<string>());

                using var transaction = new Autodesk.Revit.DB.Transaction(document, "BHS probe: schema entity");
                transaction.Start();
                var storage = Autodesk.Revit.DB.ExtensibleStorage.DataStorage.Create(document);
                storage.SetEntity(entity);
                transaction.Commit();

                var read = storage.GetEntity(schema);
                answer["schema:readBack"] = read.Get<IDictionary<string, string>>("Values")["k"];
                answer["schema:schemaGuid"] = read.SchemaGUID == probeSchemaId ? "same" : "different";

                foreach (var field in schema.ListFields())
                    answer["schema:recognized:" + field.FieldName] =
                        read.RecognizedField(field) ? "True" : "False";
            });

            answer["schema:rolledBack"] = Attempt(() => group.RollBack());
            answer["schema:clean"] = document.IsModified ? "False" : "True";

            return answer;
        }).ConfigureAwait(false);

        return report;
    }

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

            const string cleared = "Model:Probe:Cleared";

            // What the product layer put there, before the model has said anything.
            answer["model:clearedBefore"] = _services.ModelSettings.For(document)[cleared] ?? "(unset)";

            _services.ModelSettings.Write(document, new Dictionary<string, string?>
            {
                [key] = "written-by-the-probe",

                // Null means remove, not "set to empty". The subtle half of the mechanism: a project
                // takes away what the vendor's own file set, and the consumer falls back to its own
                // default rather than ours.
                [cleared] = null,
            });

            var after = _services.ModelSettings.For(document);
            answer["model:after"] = after[key] ?? "(unset)";
            answer["model:origin"] = after.Origin.ToString();

            // The other half of the rule: a key without the project prefix must not come from the
            // model at all, and must still answer from the ordinary chain.
            answer["model:userScoped"] = after["Probe:Marker"] ?? "(unset)";
            answer["model:clearedAfter"] = after[cleared] ?? "(unset)";

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
