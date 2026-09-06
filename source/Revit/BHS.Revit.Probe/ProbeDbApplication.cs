using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Revit.Host;

namespace BHS.Revit.Probe;

/// <summary>
/// The probe's second half: the same framework, in an add-in that has no user interface.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the DB form is a first-class shape of add-in here and everything said about it
/// was said from metadata. Ordinary interactive Revit loads a <c>DBApplication</c> manifest as
/// readily as an <c>Application</c> one, so the same unattended sweep that checks the interface form
/// can check this one - no headless engine, no separate run.
/// </para>
/// <para>
/// It deliberately does <b>not</b> serve a channel of its own. Two servers in one process would race
/// for one pipe name, and what is being measured here is the host, not the transport. What it sees
/// is recorded in statics; the interface half is in the same assembly and answers for both.
/// </para>
/// <para>
/// Two add-ins in one assembly is a shape a real edition may well take - one assembly, one Lib
/// folder, two entry points doing different work - and the SDK supports it for that reason. What is
/// not representative here is that both halves are the same probe measuring itself; a real pair
/// would divide the work. The arrangement is kept because it puts both forms in one Revit at once,
/// which is exactly the case the host registry has to survive.
/// </para>
/// </remarks>
public sealed class ProbeDbApplication : RevitDbAddInApplication
{
    public static readonly Guid Id = new("79613878-acf3-4f56-83fc-11412bb53c24");

    /// <summary>Whether this half started at all. The first thing worth knowing.</summary>
    public static bool Started;

    /// <summary>What the add-in id came back as, so a wrong answer is visible as a wrong answer.</summary>
    public static string AddInIdSeen = "(none)";

    /// <summary>Whether the framework already said Revit had started when this half was composed.</summary>
    /// <remarks>
    /// It must not have: <c>OnStartup</c> runs long before Revit finishes starting - measured at
    /// roughly half the time to a usable main window - and a flag that were true here would mean the
    /// framework had stopped distinguishing the two.
    /// </remarks>
    public static bool InitializedAtStart = true;

    /// <summary>Whether a settings key read from disk arrived here too.</summary>
    public static string SettingsSeen = "(none)";

    /// <summary>What asking for the API thread costs in a host that has no way onto it.</summary>
    /// <remarks>
    /// Recorded as the message rather than as a flag, because the whole argument for a separate
    /// interface over a nullable member is that the failure says what is wrong. A flag would prove
    /// that it threw; only the text proves it was worth throwing.
    /// </remarks>
    public static string UiRefusal = "(not asked)";

    /// <summary>
    /// Whether the framework's own "Revit has started" reaches a form that has no session.
    /// </summary>
    /// <remarks>
    /// It does - measured on Revit 2024 before anything was renamed, which is why the member behind
    /// it is now <c>IsInitialized</c> and not <c>IsSessionReady</c>, and why the host subscribes for
    /// both forms rather than only for the one with an interface. Kept as a check so that the
    /// correction cannot quietly come undone.
    /// </remarks>
    public static bool Initialized;

    /// <summary>The thread Revit called this form on. It has to be the one the other form saw.</summary>
    public static int ApiThreadId;

    protected override Guid AddInId => Id;

    protected override string Name => "BHS.Revit.Probe.Db";

    protected override IReadOnlyList<IFeatureModule> Modules { get; } = new IFeatureModule[] { new ProbeDbModule() };

    protected override void OnStarted(IFeatureServices services)
    {
        Started = true;
        AddInIdSeen = services.Revit.AddInId.ToString();
        InitializedAtStart = services.Revit.IsInitialized;
        ApiThreadId = services.Revit.ApiThreadId;
        SettingsSeen = services.Settings["Probe:Marker"] ?? "(none)";

        try
        {
            _ = services.Ui().Pump;
            UiRefusal = "(none - a pump was handed out, which should be impossible here)";
        }
        catch (InvalidOperationException error)
        {
            UiRefusal = error.Message;
        }

        // Through the framework's event and not Revit's own: what is being checked now is that the
        // host raises it for this form too, which is the whole point of the correction.
        services.Revit.Initialized += (_, _) => Initialized = true;

        services.Log.Info("the DB half started; initialized already: {0}", InitializedAtStart);
    }
}
