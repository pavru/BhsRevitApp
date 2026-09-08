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

    /// <summary>Adds an item under both ends, so a long run is found from either.</summary>
    /// <remarks>
    /// A tray run is metres long and lands in one cell by its start alone, which makes it invisible
    /// to a query near its other end. Indexing both ends is what makes the grid honest about
    /// segments rather than about points; a run longer than the cell is still only reachable near an
    /// end, and that is exactly right - carriers join at their ends.
    /// </remarks>
    public void AddSpan(int item, Point3 start, Point3 end)
    {
        var a = Key(start);
        var b = Key(end);

        Cell(a).Add(item);

        if (!a.Equals(b))
            Cell(b).Add(item);
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
