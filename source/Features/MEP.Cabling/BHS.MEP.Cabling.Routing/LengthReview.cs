namespace BHS.MEP.Cabling.Routing;

/// <summary>The spelling of the carriers a length was measured along.</summary>
/// <remarks>
/// <b>One rule in one place, because two sides now depend on it.</b> The apply writes this into
/// <c>BHS_Cbl_RouteStamp</c>, and the check for stale lengths reads it back and compares it with what
/// the search walks today; a second copy of the spelling would let the writer and the reader drift
/// apart in a way that reads as "every circuit is stale". Ids, each once, joined by "; "; a carrier in
/// a link is written <c>link:element</c>, which is <see cref="CarrierId.ToString"/> itself.
/// </remarks>
/// <remarks>
/// <b>Compared as a set, never as a string</b> - see <see cref="Same"/>. It said "in the order the
/// route walked them" until the cable became a tree, and a tree has no single order to walk: the
/// panel's run and a branch off it are laid in whatever order the search settled them, and two runs
/// through one tray are one carrier either way. Comparing the text would then call a circuit stale
/// for having been assembled differently, which is the failure that looks like a finding. Stamps
/// already in models were written in a walk order and are read by the same rule, so they keep
/// meaning what they meant.
/// </remarks>
public static class RouteStamp
{
    private const string Separator = "; ";

    /// <summary>The carriers of a route, as the stamp spells them.</summary>
    /// <remarks>
    /// The order is the caller's - <c>RouteResult.Path</c> is a sorted set, so what a stamp written
    /// today holds is sorted. Nothing reads it back that way, and nothing should: <see cref="Same"/>
    /// is the comparison.
    /// </remarks>
    public static string Of(IEnumerable<CarrierId> path)
    {
        var seen = new HashSet<CarrierId>();
        var parts = new List<string>();

        foreach (var id in path)
        {
            if (seen.Add(id))
                parts.Add(id.ToString());
        }

        return string.Join(Separator, parts);
    }

    /// <summary>Whether a stamp names the same carriers as a route, whatever order either is in.</summary>
    /// <remarks>
    /// <b>The question is which carriers the length was measured along, and a set answers it.</b> A
    /// stamp that lost a tray and one that gained a tray are both stale, and which it was the review
    /// already reports - <c>Left</c> and <c>Arrived</c>; a stamp holding exactly the same carriers in
    /// another order describes the same measurement and is not stale at all.
    /// </remarks>
    public static bool Same(string stamp, IEnumerable<CarrierId> path)
    {
        var stored = new HashSet<CarrierId>(Parse(stamp));
        var walked = new HashSet<CarrierId>(path ?? Array.Empty<CarrierId>());

        return stored.Count == walked.Count && stored.SetEquals(walked);
    }

    /// <summary>
    /// The carriers a stamp names, in its order; anything that does not read as one is skipped.
    /// </summary>
    /// <remarks>
    /// <b>A part nobody can read is not a reason to refuse the whole stamp.</b> What the check does
    /// with the answer is name which carriers left the route and which arrived - a list that is better
    /// short than absent, and whose caller has already decided the two stamps differ by comparing the
    /// strings. Written by an older version of us, or edited by hand, the value still says something.
    /// </remarks>
    public static IReadOnlyList<CarrierId> Parse(string stamp)
    {
        var carriers = new List<CarrierId>();

        if (string.IsNullOrEmpty(stamp))
            return carriers;

        foreach (var part in stamp.Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part.Trim();
            var colon = text.IndexOf(':');
            var source = 0L;

            if (colon >= 0)
            {
                if (!long.TryParse(text.Substring(0, colon), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out source))
                {
                    continue;
                }

                text = text.Substring(colon + 1);
            }

            if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var element))
            {
                carriers.Add(new CarrierId(source, element));
            }
        }

        return carriers;
    }
}

/// <summary>What one circuit carries from an earlier apply.</summary>
/// <remarks>
/// The three the apply writes together, read back as they stand. Absent is distinct from zero
/// throughout: a circuit that was never written carries nothing, and a zero written where a route was
/// not found is exactly the number this whole check exists to catch.
/// </remarks>
public sealed class StoredRoute
{
    public StoredRoute(CarrierId circuit, double? length, CircuitConnection? connection, string stamp)
    {
        Circuit = circuit;
        Length = length;
        Connection = connection;
        Stamp = stamp ?? string.Empty;
    }

    public CarrierId Circuit { get; }

    /// <summary>The stored length, in internal feet, or null when the circuit carries none.</summary>
    public double? Length { get; }

    /// <summary>The connection the stored length was computed with, or null when none is stored.</summary>
    public CircuitConnection? Connection { get; }

    /// <summary>The stored stamp, empty when the circuit carries none.</summary>
    public string Stamp { get; }

    /// <summary>Whether an apply ever wrote anything here.</summary>
    public bool Written => Length.HasValue || Connection.HasValue || Stamp.Length > 0;
}

/// <summary>Why a stored length no longer describes the model - the owner's four, 2026-09-17.</summary>
[Flags]
public enum StaleReason
{
    None = 0,

    /// <summary>The route runs through different carriers than the stamp names.</summary>
    CarriersDiffer = 1,

    /// <summary>The length the search finds today differs from the stored one by more than the tolerance.</summary>
    LengthDiffers = 2,

    /// <summary>The circuit is routed in a different connection than the stored length was computed with.</summary>
    ConnectionDiffers = 4,

    /// <summary>A length is stored and the search finds no route at all any more.</summary>
    NoRouteNow = 8,
}

/// <summary>One circuit whose stored length no longer describes the model.</summary>
public sealed class StaleCircuit
{
    public StaleCircuit(CarrierId circuit, StaleReason reasons)
    {
        Circuit = circuit;
        Reasons = reasons;
    }

    public CarrierId Circuit { get; }

    public StaleReason Reasons { get; }

    /// <summary>What the circuit says about itself, so the screen can name it as the search does.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>The stored length, in internal feet; zero when nothing is stored.</summary>
    public double StoredLength { get; init; }

    /// <summary>What the search finds today, in internal feet; zero when it finds no route.</summary>
    public double ComputedLength { get; init; }

    public CircuitConnection? StoredConnection { get; init; }

    public CircuitConnection ComputedConnection { get; init; }

    /// <summary>Carriers the stamp names that the route no longer walks.</summary>
    public IReadOnlyList<CarrierId> Left { get; init; } = Array.Empty<CarrierId>();

    /// <summary>Carriers the route walks now that the stamp does not name.</summary>
    public IReadOnlyList<CarrierId> Arrived { get; init; } = Array.Empty<CarrierId>();

    /// <summary>Where the search stopped, when it found no route: the address the run reports.</summary>
    public string BlockedAt { get; init; } = string.Empty;

    public bool Has(StaleReason reason) => (Reasons & reason) != 0;
}

/// <summary>
/// Which circuits carry a length that no longer describes the model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The comparison is here, on the plain axis, although both of its inputs come from Revit.</b> What
/// the model holds is read by the side that has a <c>Document</c>; what the model should hold is the
/// search's own answer; the rule that tells one from the other is arithmetic over two records, and it
/// is worth proving on networks a person can check by hand rather than only inside Revit.
/// </para>
/// <para>
/// <b>The stamp does not save the search, and that was said before the parameter was declared.</b> A
/// check for stale lengths has to read the model and route it again either way; what the stamp buys is
/// the ability to say <i>which</i> carrier left the route, and to select the stored path by id.
/// </para>
/// </remarks>
public sealed class LengthReview
{
    private LengthReview(IReadOnlyList<StaleCircuit> stale, int examined, int current, int neverWritten)
    {
        Stale = stale;
        Examined = examined;
        Current = current;
        NeverWritten = neverWritten;
    }

    /// <summary>The circuits whose stored answer no longer holds, in the order they were routed.</summary>
    public IReadOnlyList<StaleCircuit> Stale { get; }

    /// <summary>How many circuits the run offered.</summary>
    public int Examined { get; }

    /// <summary>How many carry a stored answer that still holds.</summary>
    public int Current { get; }

    /// <summary>
    /// How many routed now and carry nothing - never applied, which is not the same as stale.
    /// </summary>
    /// <remarks>
    /// Counted and shown apart rather than listed as a finding: the owner's four reasons are all about
    /// a stored answer that stopped being true, and a circuit nobody ever wrote has no such answer. A
    /// model where nothing was ever applied would otherwise report every circuit as a problem, which is
    /// the kind of report people stop reading.
    /// </remarks>
    public int NeverWritten { get; }

    public bool Any => Stale.Count > 0;

    /// <summary>
    /// Compares what the model holds with what the search found.
    /// </summary>
    /// <param name="run">
    /// This run: its results, one per circuit, and the plan that says where each cable is cut.
    /// </param>
    /// <remarks>
    /// <b>A run rather than a list of results, since 2026-09-21.</b> What makes a stored length stale
    /// is that it differs from the length computed today, and that length now includes slack counted
    /// per place the cable is cut - which the plan decides, not the route. Given only the routes, this
    /// would compare a stored total against a number missing its slack and call every circuit stale.
    /// </remarks>
    /// <param name="stored">What each circuit carries, by circuit; a circuit missing from it carries nothing.</param>
    /// <param name="tolerance">
    /// How far the two lengths may differ and still count as the same, in internal feet. A length is
    /// stored as a double and read back as one, so the difference on an unchanged model is zero - but a
    /// tolerance keeps the report from turning red over a value somebody rounded in a schedule.
    /// </param>
    /// <param name="name">
    /// What a circuit is called, for a screen that has to name it. Optional, because the search does not
    /// know: a result carries the number only when it failed, inside the address it stopped at. The side
    /// that read the model does know, and passes it here rather than joining the two lists later.
    /// </param>
    public static LengthReview Of(
        RouteRun run,
        IReadOnlyDictionary<CarrierId, StoredRoute> stored,
        double tolerance,
        Func<CarrierId, string>? name = null)
    {
        var results = run.Results;
        var stale = new List<StaleCircuit>();
        var current = 0;
        var neverWritten = 0;

        foreach (var route in results)
        {
            if (!stored.TryGetValue(route.Circuit, out var was) || !was.Written)
            {
                if (route.Status == RouteStatus.Found)
                    neverWritten++;

                continue;
            }

            if (route.Status != RouteStatus.Found)
            {
                stale.Add(new StaleCircuit(route.Circuit, StaleReason.NoRouteNow)
                {
                    Number = name?.Invoke(route.Circuit) ?? string.Empty,
                    StoredLength = was.Length ?? 0,
                    StoredConnection = was.Connection,
                    BlockedAt = route.BlockedAt,
                    Left = RouteStamp.Parse(was.Stamp),
                });

                continue;
            }

            var reasons = StaleReason.None;

            if (!RouteStamp.Same(was.Stamp, route.Path))
                reasons |= StaleReason.CarriersDiffer;

            if (was.Length is { } length && Math.Abs(length - run.TotalLengthOf(route.Circuit)) > tolerance)
                reasons |= StaleReason.LengthDiffers;

            if (was.Connection is { } connection && connection != route.Connection)
                reasons |= StaleReason.ConnectionDiffers;

            if (reasons == StaleReason.None)
            {
                current++;
                continue;
            }

            var before = RouteStamp.Parse(was.Stamp);
            var now = route.Path;

            stale.Add(new StaleCircuit(route.Circuit, reasons)
            {
                Number = name?.Invoke(route.Circuit) ?? string.Empty,
                StoredLength = was.Length ?? 0,
                ComputedLength = run.TotalLengthOf(route.Circuit),
                StoredConnection = was.Connection,
                ComputedConnection = route.Connection,
                Left = Missing(before, now),
                Arrived = Missing(now, before),
            });
        }

        return new LengthReview(stale, results.Count, current, neverWritten);
    }

    /// <summary>What the first list names and the second does not, in the first list's order.</summary>
    private static IReadOnlyList<CarrierId> Missing(IEnumerable<CarrierId> these, IEnumerable<CarrierId> those)
    {
        var known = new HashSet<CarrierId>(those);
        var missing = new List<CarrierId>();

        foreach (var id in these)
        {
            if (!known.Contains(id) && !missing.Contains(id))
                missing.Add(id);
        }

        return missing;
    }
}
