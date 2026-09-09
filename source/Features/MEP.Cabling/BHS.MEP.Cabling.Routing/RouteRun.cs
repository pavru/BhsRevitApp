namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// What one pass of the search produced, with the failures grouped rather than counted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grouping is the whole point, and it is why <see cref="RouteStatus"/> exists.</b> The
/// predecessor returned an empty list for three unrelated reasons, so a report could say "twelve
/// circuits did not compute" and nothing more. Twelve failures are two or three causes; a person can
/// act on a cause and cannot act on a number.
/// </para>
/// <para>
/// It lives beside the search rather than in the view model, because it is about routes. A view
/// model that worked these out would be a second place where "what went wrong" is decided, and two
/// such places disagree within a month. The view model formats what is here; it does not compute it.
/// </para>
/// <para>
/// <b>The counts are walked once, in the constructor, rather than answered from properties.</b> The
/// screen asks for four of them and the failure list asks again, and on a run of several hundred
/// circuits that is six passes to answer questions whose answer cannot change: everything here is
/// finished by the time it is built.
/// </para>
/// </remarks>
public sealed class RouteRun
{
    private readonly int[] _byStatus;

    public RouteRun(IReadOnlyList<RouteResult> results, long networkVersion, TimeSpan took)
    {
        Results = results;
        NetworkVersion = networkVersion;
        Took = took;

        var failures = new List<RouteResult>();
        var statuses = new int[Enum.GetValues(typeof(RouteStatus)).Length];
        var length = 0.0;
        var builtIn = 0.0;

        foreach (var one in results)
        {
            var status = (int)one.Status;

            if (status >= 0 && status < statuses.Length)
                statuses[status]++;

            if (one.Status == RouteStatus.Found)
            {
                length += one.TotalLength;
                builtIn += one.BuiltInLength;
            }
            else
            {
                failures.Add(one);
            }
        }

        _byStatus = statuses;
        Failures = failures;
        TotalLength = length;
        BuiltInLength = builtIn;
    }

    public IReadOnlyList<RouteResult> Results { get; }

    /// <summary>The network these were computed on, so a later answer can say it is stale.</summary>
    public long NetworkVersion { get; }

    /// <summary>
    /// What the side that read the model wants said, in its own words.
    /// </summary>
    /// <remarks>
    /// <b>Strings, carried and never read.</b> What a link is, and what it means for one to be
    /// placed but not loaded, is knowledge the Revit side has and the search does not. Composing
    /// them here would need that knowledge; leaving them out would mean a run reports half its
    /// circuits unroutable while the reason sits in a count nobody was shown.
    /// </remarks>
    public IReadOnlyList<string> Reading { get; init; } = Array.Empty<string>();

    /// <summary>What other join tolerances would have made of the same carriers.</summary>
    /// <remarks>
    /// Empty unless something failed to cross the structure. It is evidence rather than advice: a
    /// wider tolerance joins runs a person reads as joined, and also joins two that merely pass
    /// near each other - so the number to choose is a judgement about a model, and this is the table
    /// it is made from.
    /// </remarks>
    public IReadOnlyList<ToleranceReading> Tolerances { get; init; } = Array.Empty<ToleranceReading>();

    /// <summary>What that network looked like as a graph.</summary>
    /// <remarks>
    /// Carried on the run rather than fetched from the network by whoever displays it, because the
    /// network is not kept once the run is over - and the one question this answers is asked exactly
    /// when the run reports that it could not cross the structure.
    /// </remarks>
    public NetworkShape Shape { get; init; }

    /// <summary>How long the search itself took, without the reading that preceded it.</summary>
    /// <remarks>
    /// Apart from the read on purpose. Reading a model is dominated by how big the model is and how
    /// tired the machine is - this repository has measured the same wait at 16 s and at 696 s - while
    /// the search is dominated by what we wrote. Folded together, a regression in ours would hide
    /// inside the variance of theirs.
    /// </remarks>
    public TimeSpan Took { get; }

    public int Count(RouteStatus status)
    {
        var index = (int)status;
        return index >= 0 && index < _byStatus.Length ? _byStatus[index] : 0;
    }

    public int Found => Count(RouteStatus.Found);

    /// <summary>Total computed length, in internal feet, over the circuits that routed.</summary>
    public double TotalLength { get; }

    /// <summary>What Revit reports today for those same circuits.</summary>
    /// <remarks>
    /// Summed over the ones that routed, and only those - see <see cref="RouteResult.BuiltInLength"/>
    /// for why the pair travels on the result rather than being gathered from two sets here.
    /// </remarks>
    public double BuiltInLength { get; }

    /// <summary>The circuits that did not route, in the order they were tried.</summary>
    /// <remarks>
    /// Kept whole rather than summarised: the screen groups them by status, and a person then wants
    /// to know <i>which</i> circuit and <i>where</i> it stopped. <see cref="RouteResult.BlockedAt"/>
    /// carries the address for exactly this.
    /// </remarks>
    public IReadOnlyList<RouteResult> Failures { get; }

    /// <summary>The failures of one kind, for a screen that lists causes before circuits.</summary>
    public IEnumerable<RouteResult> Blocked(RouteStatus status) =>
        Failures.Where(one => one.Status == status);

    /// <summary>Every status that actually occurred, worst first, so a report can walk them.</summary>
    /// <remarks>
    /// <b>Only the ones that occurred.</b> A report that prints "NoConnectivity: 0" alongside the
    /// two causes that did happen buries them, and this repository has already written down what a
    /// report that speaks about nothing costs: it stops being read, and then it cannot speak about
    /// something either.
    /// </remarks>
    public IEnumerable<RouteStatus> Causes
    {
        get
        {
            foreach (var status in new[] { RouteStatus.NoCarrierNear, RouteStatus.NoConnectivity, RouteStatus.NothingToRoute })
            {
                if (Count(status) > 0)
                    yield return status;
            }
        }
    }
}

/// <summary>What one join tolerance makes of a set of carriers.</summary>
public readonly struct ToleranceReading
{
    public ToleranceReading(double tolerance, NetworkShape shape)
    {
        Tolerance = tolerance;
        Shape = shape;
    }

    /// <summary>The tolerance tried, in internal feet - the screen formats it as a length.</summary>
    public double Tolerance { get; }

    public NetworkShape Shape { get; }
}
