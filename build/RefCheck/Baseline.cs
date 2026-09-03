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
/// One <c>bindingRedirect</c> out of <c>Revit.exe.config</c>.
/// </summary>
/// <remarks>
/// Part of the model rather than a detail, because without redirects the diagnosis is wrong in both
/// directions. Measured twice on Revit 2024: <c>Newtonsoft.Json</c> is dangerous <em>because</em> a
/// redirect collapses both copies onto one identity, and <c>System.ComponentModel.Annotations</c> is
/// safe <em>because</em> a redirect covers it - a tool that reads neither calls the first harmless
/// and the second broken.
/// <para>
/// Only meaningful on the .NET Framework axis. Revit 2025 and later run on .NET, where there are no
/// redirects at all and the simple name goes to whoever loaded it first.
/// </para>
/// </remarks>
internal sealed class BaselineRedirect
{
    public string Name { get; set; } = "";

    public string PublicKeyToken { get; set; } = "";

    public string OldVersionLow { get; set; } = "";

    public string OldVersionHigh { get; set; } = "";

    public string NewVersion { get; set; } = "";

    /// <summary>Whether a reference to <paramref name="version"/> is caught by this redirect.</summary>
    public bool Covers(string name, Version version)
    {
        if (!string.Equals(name, Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return Version.TryParse(OldVersionLow, out var low)
               && Version.TryParse(OldVersionHigh, out var high)
               && version >= low
               && version <= high;
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

    public string DeniedComment { get; set; } = "";

    /// <summary>
    /// Simple names that must not appear in a Revit-side output at all, in any version.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="Assemblies"/>, and answered separately on purpose.
    /// The watchlist asks "if Revit substitutes its copy for ours, is every member still there";
    /// this asks "are we entitled to ship this assembly at all". A name may be on both lists
    /// without contradiction, and this one wins: the surfaces of
    /// <c>Microsoft.Extensions.Configuration</c> matched perfectly and the add-in still would not
    /// load on Revit 2026, because the failure is in binding a name to a file, one level below
    /// anything a surface comparison can see.
    /// <para>
    /// A trailing <c>*</c> matches a prefix; anything else is the whole name. Versions are not
    /// consulted, and that is the point - whoever loads first owns the simple name.
    /// </para>
    /// </remarks>
    public List<string> Denied { get; set; } = [];

    public static Watchlist Load(string path)
    {
        var watchlist = JsonSerializer.Deserialize<Watchlist>(File.ReadAllText(path), Baseline.JsonOptions)
                        ?? throw new InvalidDataException($"'{path}' is not a RefCheck watchlist.");
        return watchlist;
    }

    public IReadOnlySet<string> ToSet() => new HashSet<string>(Assemblies, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether one assembly's simple name is forbidden in a Revit-side output.</summary>
    public bool IsDenied(string simpleName)
    {
        foreach (var pattern in Denied)
        {
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                if (simpleName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(simpleName, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
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

    /// <summary>Binding redirects from <c>Revit.exe.config</c>. Empty on the .NET axis.</summary>
    public List<BaselineRedirect> Redirects { get; set; } = [];

    /// <summary>
    /// Whether this release runs on .NET Framework, where the binding rules are different.
    /// </summary>
    /// <remarks>
    /// The two axes fail differently and the conclusion does not carry across, which is measured:
    /// on .NET a request for a higher version is refused outright - that is what stopped an add-in
    /// loading on Revit 2026 - while on .NET Framework the binder answered a request for
    /// <c>System.Memory</c> 4.0.2.0 with a 4.0.1.1 file out of another vendor's add-in folder.
    /// </remarks>
    [JsonIgnore]
    public bool IsNetFramework =>
        int.TryParse(RevitVersion, out var year) && year <= 2024;

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
    /// <summary>Reads the <c>bindingRedirect</c> elements out of Revit's own configuration.</summary>
    private static IEnumerable<BaselineRedirect> ReadRedirects(string configPath)
    {
        if (!File.Exists(configPath)) yield break;

        System.Xml.Linq.XDocument document;
        try
        {
            document = System.Xml.Linq.XDocument.Load(configPath);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        System.Xml.Linq.XNamespace ns = "urn:schemas-microsoft-com:asm.v1";

        foreach (var dependent in document.Descendants(ns + "dependentAssembly"))
        {
            var identity = dependent.Element(ns + "assemblyIdentity");
            var redirect = dependent.Element(ns + "bindingRedirect");

            if (identity is null || redirect is null) continue;

            var range = (redirect.Attribute("oldVersion")?.Value ?? "").Split('-');

            yield return new BaselineRedirect
            {
                Name = identity.Attribute("name")?.Value ?? "",
                PublicKeyToken = identity.Attribute("publicKeyToken")?.Value ?? "",
                OldVersionLow = range.Length > 0 ? range[0] : "",
                OldVersionHigh = range.Length > 1 ? range[1] : (range.Length > 0 ? range[0] : ""),
                NewVersion = redirect.Attribute("newVersion")?.Value ?? ""
            };
        }
    }

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

        baseline.Redirects.AddRange(ReadRedirects(Path.Combine(revitDirectory, "Revit.exe.config")));

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
