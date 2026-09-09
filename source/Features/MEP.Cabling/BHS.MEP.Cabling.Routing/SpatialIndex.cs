namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// Finds what is near a point, without looking at everything.
/// </summary>
/// <remarks>
/// <para>
/// A uniform grid of cells, keyed by rounded coordinates. Not a tree: the thing being indexed is
/// building structure, which is spread thinly and evenly through a box rather than clustered, and a
/// grid over that answers in constant time with a dictionary and no balancing.
/// </para>
/// <para>
/// <b>It is here from the first line because adjacency is by proximity.</b> Joining carriers by how
/// close they are - rather than by whether Revit says they are connected - means every carrier has
/// to find its neighbours, and a pairwise scan over thousands of elements is quadratic: millions of
/// comparisons for a model of a few thousand trays, on the thread the user is watching. The index is
/// not an optimisation to add when somebody complains; without it the feature does not work at the
/// size it was designed for.
/// </para>
/// <para>
/// Cell size is the search radius, so a query touches twenty-seven cells and no more. Larger cells
/// make each one crowded; smaller ones multiply the lookups without reducing the work.
/// </para>
/// </remarks>
internal sealed class SpatialIndex
{
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = new();
    private readonly double _cell;

    public SpatialIndex(double cellSize)
    {
        // A cell of zero would put everything in one bucket and quietly restore the quadratic scan
        // this class exists to avoid - so it is a floor rather than an argument check: the caller's
        // tolerance may legitimately be zero, meaning "touching", and the index still has to work.
        _cell = cellSize > 1e-9 ? cellSize : 1e-9;
    }

    public void Add(int item, Point3 at) => Cell(Key(at)).Add(item);

    /// <summary>Adds an item under every point at which it can be met.</summary>
    /// <remarks>
    /// Replaces the two-ended form. A tee is met at three points and a cross at four, and indexing
    /// only the two that lie farthest apart makes the branch invisible to a query standing on it -
    /// which is a hole in the network that reports itself later as somebody's missing route.
    /// Duplicate cells are skipped, so a short fitting whose terminals share one cell is listed once.
    /// <para>
    /// The skipping is done with a set rather than by comparing each point against the ones before
    /// it. A fitting may carry any number of connectors - the owner's correction, and the reason
    /// nothing in this code counts them - and the pairwise form would be quadratic in a number we
    /// have decided not to bound.
    /// </para>
    /// </remarks>
    public void AddAll(int item, IReadOnlyList<Point3> points)
    {
        if (points.Count == 1)
        {
            Cell(Key(points[0])).Add(item);
            return;
        }

        var seen = new HashSet<(int X, int Y, int Z)>();

        foreach (var at in points)
        {
            var key = Key(at);

            if (seen.Add(key))
                Cell(key).Add(item);
        }
    }

    /// <summary>Everything indexed within one cell of the point, in no particular order.</summary>
    public IEnumerable<int> Near(Point3 at)
    {
        var (x, y, z) = Key(at);

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (!_cells.TryGetValue((x + dx, y + dy, z + dz), out var found))
                continue;

            foreach (var item in found)
                yield return item;
        }
    }

    private List<int> Cell((int X, int Y, int Z) key)
    {
        if (_cells.TryGetValue(key, out var found))
            return found;

        var made = new List<int>();
        _cells[key] = made;
        return made;
    }

    private (int X, int Y, int Z) Key(Point3 at) =>
        ((int)Math.Floor(at.X / _cell), (int)Math.Floor(at.Y / _cell), (int)Math.Floor(at.Z / _cell));
}
