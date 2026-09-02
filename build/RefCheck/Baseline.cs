using System.Text.Json;
using System.Text.Json.Serialization;

namespace BimHouse.RefCheck;

/// <summary>One assembly shipped inside a Revit installation.</summary>
internal sealed class BaselineAssembly
{
    public string Name { get; set; } = "";

    public string AssemblyVersion { get; set; } = "";

    public string FileVersion { get; set; } = "";

    /// <summary>AssemblyCompanyAttribute, as read from the file.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>
    /// False for assemblies owned by the host vendor. Those are referenced through the
    /// Revit API packages, which repackage these very files, so no version skew is possible
    /// and indexing their surface would only bloat the baseline.
    /// </summary>
    public bool SurfaceIndexed { get; set; }

    /// <summary>
    /// A .NET Framework facade that only forwards types. References to it resolve through the
    /// forward, never to this file, so it can never be the assembly that wins over ours.
    /// </summary>
    public bool TypeForwarderOnly { get; set; }

    /// <summary>"Type::Member/arity" for methods, "Type::Member/field" for fields.</summary>
    public List<string> Members { get; set; } = [];

    [JsonIgnore]
    public HashSet<string> MemberSet { get; private set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public HashSet<string> MemberNameSet { get; private set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public HashSet<string> TypeSet { get; private set; } = new(StringComparer.Ordinal);

    public void BuildLookups()
    {
        MemberSet = new HashSet<string>(Members, StringComparer.Ordinal);
        MemberNameSet = new HashSet<string>(StringComparer.Ordinal);
        TypeSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in Members)
        {
            var slash = member.LastIndexOf('/');
            if (slash > 0) MemberNameSet.Add(member[..slash]);

            var separator = member.IndexOf("::", StringComparison.Ordinal);
            if (separator > 0) TypeSet.Add(member[..separator]);
        }
    }
}

/// <summary>
/// The assemblies whose member surface is worth storing: the ones a Revit-side project
/// actually redistributes. Shared by every Revit version, so it lives in its own file.
/// </summary>
internal sealed class Watchlist
{
    public string Comment { get; set; } = "";

    public List<string> Assemblies { get; set; } = [];

    public static Watchlist Load(string path)
    {
        var watchlist = JsonSerializer.Deserialize<Watchlist>(File.ReadAllText(path), Baseline.JsonOptions)
                        ?? throw new InvalidDataException($"'{path}' is not a RefCheck watchlist.");
        return watchlist;
    }

    public IReadOnlySet<string> ToSet() => new HashSet<string>(Assemblies, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Everything one Revit version ships, as of the moment it was collected.</summary>
internal sealed class Baseline
{
    public string RevitVersion { get; set; } = "";

    public string SourcePath { get; set; } = "";

    public string CollectedUtc { get; set; } = "";

    /// <summary>Assemblies whose Vendor contains this marker are listed but not indexed.</summary>
    public string HostVendorMarker { get; set; } = "Autodesk";

    public List<BaselineAssembly> Assemblies { get; set; } = [];

    [JsonIgnore]
    public Dictionary<string, BaselineAssembly> ByName { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static Baseline Load(string path)
    {
        var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException($"'{path}' is not a RefCheck baseline.");

        baseline.ByName = new Dictionary<string, BaselineAssembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in baseline.Assemblies)
        {
            assembly.BuildLookups();
            baseline.ByName[assembly.Name] = assembly;
        }

        return baseline;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Walks a Revit installation directory and records what it ships. Identity is recorded for
    /// every managed assembly; the member surface only for those named in <paramref name="watched"/>,
    /// because indexing all of them costs megabytes for assemblies no add-in would ever redistribute.
    /// </summary>
    public static Baseline Collect(
        string revitDirectory,
        string revitVersion,
        string hostVendorMarker,
        IReadOnlySet<string> watched)
    {
        var baseline = new Baseline
        {
            RevitVersion = revitVersion,
            SourcePath = revitDirectory,
            CollectedUtc = DateTime.UtcNow.ToString("O"),
            HostVendorMarker = hostVendorMarker
        };

        var files = Directory
            .EnumerateFiles(revitDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(revitDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            AssemblyIndex? index;
            try
            {
                index = AssemblyIndex.Read(file);
            }
            catch (BadImageFormatException)
            {
                continue; // native library
            }
            catch (IOException)
            {
                continue;
            }

            if (index is null) continue;

            var isHostVendor = index.Vendor.Contains(hostVendorMarker, StringComparison.OrdinalIgnoreCase);
            var indexSurface = !isHostVendor && watched.Contains(index.Name);

            baseline.Assemblies.Add(new BaselineAssembly
            {
                Name = index.Name,
                AssemblyVersion = index.AssemblyVersion,
                FileVersion = index.FileVersion,
                Vendor = index.Vendor,
                TypeForwarderOnly = index.IsTypeForwarderOnly,
                SurfaceIndexed = indexSurface,
                Members = indexSurface ? index.Members.Order(StringComparer.Ordinal).ToList() : []
            });
        }

        return baseline;
    }
}
