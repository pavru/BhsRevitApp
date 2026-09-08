using System.Globalization;

namespace BHS.Settings;

/// <summary>Which side of the process boundary is reading.</summary>
public enum ProcessSide
{
    /// <summary>Loaded into Revit.</summary>
    Revit,

    /// <summary>A companion process of our own.</summary>
    WinSide,
}

/// <summary>Where a settings file sits, which is what decides who may write it and who it is for.</summary>
public enum SettingsLayerKind
{
    /// <summary>Shipped beside the assembly. Defaults, replaced by the next install.</summary>
    Product,

    /// <summary>Under <c>%ProgramData%</c>. One answer for everybody on this machine.</summary>
    Machine,

    /// <summary>Under <c>%AppData%</c>. This person's own answer.</summary>
    User,

    /// <summary>Environment variables, and whatever the host passed in by hand.</summary>
    Process,
}

/// <summary>One layer, named so that diagnostics can say where a value came from.</summary>
public sealed class SettingsLayer
{
    internal SettingsLayer(SettingsLayerKind kind, string path)
    {
        Kind = kind;
        Path = path;
    }

    public SettingsLayerKind Kind { get; }

    /// <summary>Full path of the file, or a description for a layer that is not a file.</summary>
    public string Path { get; }

    public bool Exists => Kind != SettingsLayerKind.Process && File.Exists(Path);

    public override string ToString() =>
        $"{Kind,-7} {Path}" + (Kind == SettingsLayerKind.Process || Exists ? string.Empty : "   (absent)");
}

/// <summary>
/// Works out which files are read, in which order.
/// </summary>
/// <remarks>
/// Three directories, and within each up to three files of increasing specificity: what both sides
/// share, what one side alone needs, and - Revit-side only - what one release alone needs. Later
/// wins, so the ordering reads as a sentence: what we shipped, overruled by what this machine says,
/// overruled by what this person says, overruled by this process's own environment.
/// <para>
/// The vendor directory is <c>BHS</c> under <c>%ProgramData%</c> and <c>%AppData%</c> rather than
/// anything Revit-shaped, and deliberately so: the two sides have to arrive at the same file, and
/// one of them has never heard of Revit.
/// </para>
/// <para>
/// The per-release file is worth having even though a Revit-side assembly is already installed per
/// release. The product layer is indeed per release; the machine and user layers are not, and
/// "on 2024 only, wait longer" is the kind of thing this repository has already had to say.
/// </para>
/// </remarks>
public static class SettingsLayout
{
    /// <summary>The directory name used under <c>%ProgramData%</c> and <c>%AppData%</c>.</summary>
    public const string VendorDirectory = "BHS";

    /// <summary>Base name of every settings file.</summary>
    public const string BaseName = "appsettings";

    public static string MachineDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), VendorDirectory);

    public static string UserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), VendorDirectory);

    /// <summary>
    /// Where the shipped defaults are: beside this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This assembly rather than the entry one, because inside Revit the entry assembly is Revit.
    /// <c>AppContext.BaseDirectory</c> has the same problem and is only the fallback, for the case
    /// where a location is not available at all.
    /// </para>
    /// <para>
    /// <b>Right only while this assembly is not shared, which inside Revit it is.</b> Two of our
    /// add-ins each ship a copy of <c>BHS.Settings.dll</c>, one of them wins the simple name in
    /// Revit's AppDomain, and from then on this property names <i>that</i> add-in's folder for
    /// everybody. Measured: on three releases the probe's copy won and the answer looked correct;
    /// on Revit 2027 the edition's won, and the probe reported a product layer it was not reading.
    /// </para>
    /// <para>
    /// So a Revit-side caller names its directory instead - see <c>SettingsOptions.ProductDirectory</c>
    /// - and takes it from its own add-in assembly. This default belongs to a process with one copy
    /// of this code in it, which Win-side is and Revit-side is not.
    /// </para>
    /// </remarks>
    public static string ProductDirectory { get; } = ResolveProductDirectory();

    /// <summary>The files that would be read, in the order they are read, whether or not they exist.</summary>
    public static IReadOnlyList<SettingsLayer> Files(ProcessSide side, int? release, string? productDirectory = null)
    {
        var layers = new List<SettingsLayer>();

        Add(SettingsLayerKind.Product, productDirectory ?? ProductDirectory);
        Add(SettingsLayerKind.Machine, MachineDirectory);
        Add(SettingsLayerKind.User, UserDirectory);

        return layers;

        void Add(SettingsLayerKind kind, string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            foreach (var name in Names(side, release))
                layers.Add(new SettingsLayer(kind, Path.Combine(directory, name)));
        }
    }

    /// <summary>File names within one directory, least specific first.</summary>
    public static IEnumerable<string> Names(ProcessSide side, int? release)
    {
        yield return BaseName + ".json";
        yield return BaseName + "." + Qualifier(side) + ".json";

        if (side == ProcessSide.Revit && release is > 0)
            yield return BaseName + "." + Qualifier(side) + release.Value.ToString(CultureInfo.InvariantCulture) + ".json";
    }

    /// <summary>What a side is called in a file name.</summary>
    public static string Qualifier(ProcessSide side) => side == ProcessSide.Revit ? "revit" : "winside";

    private static string ResolveProductDirectory()
    {
        try
        {
            var location = typeof(SettingsLayout).Assembly.Location;

            if (!string.IsNullOrEmpty(location))
                return Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        }
        catch (NotSupportedException)
        {
            // A dynamic or single-file assembly has no location to give.
        }

        return AppContext.BaseDirectory;
    }
}
