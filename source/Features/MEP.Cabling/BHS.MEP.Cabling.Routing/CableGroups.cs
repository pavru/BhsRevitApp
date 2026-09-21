namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// The cable groups a carrier admits, and the one rule that says whether a circuit may lie in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all - the owner, 2026-09-22.</b> There are rules forbidding cables to share
/// a route: a fire alarm run goes on its own, and structured cabling may share a tray with power only
/// where a divider separates them. That is not a question of length, it is a question of whether the
/// route is legal, and a tool that reports a shorter illegal route is worse than one that reports
/// nothing.
/// </para>
/// <para>
/// <b>The rule is one sentence, and it is written here once:</b> a circuit may lie in a carrier if
/// the carrier <i>names its group</i>. Everything else follows from what an unmarked carrier is taken
/// to name - and it names exactly one group, <see cref="string.Empty"/>. So an unmarked circuit goes
/// into unmarked carriers and nowhere else, a named circuit goes only where it is named, and a model
/// where nobody filled anything in behaves exactly as it did before this existed. Three behaviours,
/// one <c>Contains</c>; there is no second place to forget.
/// </para>
/// <para>
/// <b>The strict reading, and it was the owner's choice between two.</b> The permissive one - an
/// unmarked carrier admits anybody - protects whoever marked the trays; this one protects whoever
/// marked the circuits. They differ on exactly one cell: a fire alarm circuit over a tray nobody
/// marked. Permissive lets it through, so keeping fire alarm apart would mean marking every other
/// tray in the model and never forgetting one. Strict turns it away, which is the loud answer, and a
/// forgotten element then produces a report rather than a silent violation.
/// </para>
/// <para>
/// <b>Compared without case and without surrounding space, and never translated.</b> The values are
/// the project's own words - the owner writes them - so they are data a designer types twice in two
/// places, and the two places have to agree despite a capital letter or a stray space. They are never
/// localised for the same reason no value of ours is: the human reads the parameter's name, the code
/// compares its value.
/// </para>
/// </remarks>
public sealed class CableGroups
{
    private static readonly char[] Separators = { ';' };

    private readonly HashSet<string> _named;

    private CableGroups(IEnumerable<string> named) =>
        _named = new HashSet<string>(named, StringComparer.OrdinalIgnoreCase);

    /// <summary>What a carrier that named nobody admits: circuits that are themselves unmarked.</summary>
    /// <remarks>
    /// <b>Not "everybody", and that is the whole of the strict rule.</b> It names one group, the
    /// nameless one, so it is not a special case in <see cref="Admits"/> - it is an ordinary member
    /// of the set, and the comparison never has to ask whether anything is empty.
    /// </remarks>
    public static CableGroups Unmarked { get; } = new(new[] { string.Empty });

    /// <summary>The groups named, each once, or <see cref="Unmarked"/> when none were.</summary>
    public IReadOnlyCollection<string> Named => _named;

    /// <summary>Whether this admits only circuits nobody put in a group.</summary>
    public bool IsUnmarked => _named.Count == 1 && _named.Contains(string.Empty);

    /// <summary>The groups a parameter value names, separated by semicolons.</summary>
    /// <remarks>
    /// The same spelling as <c>BHS_Cbl_CircuitRefs</c> writes, because a designer who has seen one
    /// list in this toolset should not have to learn a second punctuation for the next. Empty entries
    /// are dropped rather than kept as the nameless group: <c>"tray; "</c> is a trailing separator,
    /// not a statement that unmarked circuits are welcome too.
    /// </remarks>
    public static CableGroups Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Unmarked;

        var named = text!
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(one => one.Trim())
            .Where(one => one.Length != 0)
            .ToArray();

        return named.Length == 0 ? Unmarked : new CableGroups(named);
    }

    /// <summary>What admits exactly one group and nothing else.</summary>
    /// <remarks>
    /// For a box the calculation recommends: it has no type to read and no designer has seen it yet,
    /// so it is a box for the group that asked for it. That is what keeps a recommendation from
    /// becoming the one place in the model where two groups are spliced together - the very thing the
    /// rule exists to prevent, arrived at by the tool's own advice rather than by anybody's drawing.
    /// </remarks>
    public static CableGroups ForOne(string? group)
    {
        var one = Normalise(group);

        return one.Length == 0 ? Unmarked : new CableGroups(new[] { one });
    }

    /// <summary>What a circuit's own value means once the spacing around it is discounted.</summary>
    public static string Normalise(string? group) => group is null ? string.Empty : group.Trim();

    /// <summary>Whether a circuit of this group may lie in the carrier that admits these.</summary>
    public bool Admits(string? group) => _named.Contains(Normalise(group));

    /// <summary>The groups named, in a form a person reads, for the screen and for notes.</summary>
    public override string ToString() =>
        IsUnmarked ? string.Empty : string.Join("; ", _named.OrderBy(one => one, StringComparer.OrdinalIgnoreCase));
}
