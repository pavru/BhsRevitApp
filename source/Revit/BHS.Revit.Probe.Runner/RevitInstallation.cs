using System.Globalization;
using BHS.Shared;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// One Revit found on this machine, and the places its add-ins live.
/// </summary>
/// <remarks>
/// Discovered by looking rather than by listing: which releases are installed changes, and a list
/// in the source would be wrong the first time one is added or removed.
/// </remarks>
internal sealed class RevitInstallation
{
    private RevitInstallation(RevitRelease release, string executablePath)
    {
        Release = release;
        ExecutablePath = executablePath;
        InstallDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

        AddInsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", release.Year.ToString(CultureInfo.InvariantCulture));

        ManifestPath = Path.Combine(AddInsDirectory, ProbeDeployment.ManifestFileName);
        ProbeDirectory = Path.Combine(AddInsDirectory, ProbeDeployment.VendorId, ProbeDeployment.AddInName);

        TrustKeyPath = $@"SOFTWARE\Autodesk\Revit\Autodesk Revit {release.Year.ToString(CultureInfo.InvariantCulture)}\CodeSigning";
    }

    public RevitRelease Release { get; }

    public string ExecutablePath { get; }

    public string InstallDirectory { get; }

    /// <summary>Per-user add-in folder for this release: %AppData%, not %ProgramData%.</summary>
    public string AddInsDirectory { get; }

    public string ManifestPath { get; }

    public string ProbeDirectory { get; }

    /// <summary>
    /// Where this release records the add-ins the user has agreed to load, under HKCU.
    /// </summary>
    /// <remarks>
    /// One DWORD per add-in, named by its <c>AddInId</c> and set to 1. It is what the "Always
    /// load" button of Revit's unsigned-add-in dialog writes, and without it an unsigned add-in
    /// stops a headless start dead: the dialog is modal, its default answer is "do not load", and
    /// nothing outside the process can answer it.
    /// </remarks>
    public string TrustKeyPath { get; }

    public bool IsProbeDeployed => File.Exists(ManifestPath) && Directory.Exists(ProbeDirectory);

    public bool IsProbeTrusted => ProbeInstaller.IsTrusted(this);

    /// <summary>Every Revit installed under Program Files, oldest first.</summary>
    public static IReadOnlyList<RevitInstallation> Discover()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
        var found = new List<RevitInstallation>();

        if (!Directory.Exists(root))
            return found;

        foreach (var directory in Directory.GetDirectories(root, "Revit *"))
        {
            var year = Path.GetFileName(directory).Substring("Revit ".Length);

            // "Revit Content Libraries 2025" and friends live here too; only a year is a release.
            if (!RevitRelease.TryParse(year, out var release))
                continue;

            var executable = Path.Combine(directory, "Revit.exe");
            if (File.Exists(executable))
                found.Add(new RevitInstallation(release, executable));
        }

        found.Sort((left, right) => left.Release.CompareTo(right.Release));
        return found;
    }
}

/// <summary>Names the build produces and this runner has to find again.</summary>
/// <remarks>
/// They come from the <c>RevitAddIn</c> item in the probe's project file and the vendor identity
/// in <c>Directory.Build.props</c>. Mirrored here rather than read back, and wrong values fail
/// visibly at the deployment check rather than quietly at launch.
/// </remarks>
internal static class ProbeDeployment
{
    public const string VendorId = "BimHouseSoftware";
    public const string AddInName = "BHS.Revit.Probe";
    public const string ManifestFileName = AddInName + ".addin";

    /// <summary>The probe's own <c>AddInId</c>, as written into the manifest by the SDK.</summary>
    public const string AddInId = "6f2e17ae-5ff7-45b2-bb8b-3482446e9a67";

    /// <summary>The probe's project, relative to the repository root.</summary>
    public const string ProjectPath = @"source\Revit\BHS.Revit.Probe\BHS.Revit.Probe.csproj";
}
