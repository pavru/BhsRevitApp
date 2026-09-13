using System.Threading;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe.Declaration;

/// <summary>
/// The rule behind the probe's Entry button: a document is present. It also counts how Revit asks it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer is ordinary; the counters are the measurement.</b> Three things about an
/// availability class in a feature's Entry assembly are unknown until Revit runs one, and each leaves a
/// trace here: whether Revit calls it at all when its logic lives in a generic base in another
/// assembly, which thread it calls on, and when it first did - so that the sweep can put that moment
/// beside the moment the Entry assembly loaded.
/// </para>
/// <para>
/// <b>Static, because nothing else survives.</b> The base builds a new rule on every call, which is
/// what makes a real rule stateless by construction, so an instance field would count to one forever.
/// Counters are the one kind of state the owner allowed a probe rule, and they are allowed nowhere
/// else: never in an Entry class, never in a feature's real rule.
/// </para>
/// <para>
/// <b>The thread is compared with <see cref="LogRouter.PrimaryThreadId"/></b>, which the host sets to
/// the API thread on the first line of <c>OnStartup</c>. Were it still zero, every call would count as
/// off the API thread - which is why the first call's own thread is kept too, so the two numbers can be
/// read against each other rather than trusted.
/// </para>
/// <para>
/// Nothing here blocks or allocates beyond one log line on the first call, and that line is
/// <c>Info</c>: Revit asks availability "any time there is a contextual change", and <c>Warning</c> and
/// above go to the Revit journal from the API thread, which inside this callback is not measured.
/// </para>
/// </remarks>
public sealed class GateRule : IAvailabilityRule
{
    private static int _calls;
    private static int _offApiThread;
    private static int _firstCallThreadId;
    private static long _firstCallTicks;

    /// <summary>How many times Revit asked, through the Entry assembly's availability class.</summary>
    public static int Calls => Volatile.Read(ref _calls);

    /// <summary>How many of those calls arrived on a thread other than the API thread.</summary>
    public static int CallsOffApiThread => Volatile.Read(ref _offApiThread);

    /// <summary>The managed thread the first call arrived on, or zero before one did.</summary>
    public static int FirstCallThreadId => Volatile.Read(ref _firstCallThreadId);

    /// <summary>When Revit first asked, in UTC, or null before it did.</summary>
    public static DateTime? FirstCallUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstCallTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public bool IsAvailable(AvailabilityContext context)
    {
        var thread = Environment.CurrentManagedThreadId;

        if (thread != LogRouter.PrimaryThreadId)
            Interlocked.Increment(ref _offApiThread);

        if (Interlocked.Increment(ref _calls) == 1)
        {
            Interlocked.CompareExchange(ref _firstCallTicks, DateTime.UtcNow.Ticks, 0);
            Interlocked.CompareExchange(ref _firstCallThreadId, thread, 0);

            Log.For<GateRule>().Info(
                "the Entry assembly's availability rule was asked for the first time, on thread {0}", thread);
        }

        return context.Document is not null;
    }
}
