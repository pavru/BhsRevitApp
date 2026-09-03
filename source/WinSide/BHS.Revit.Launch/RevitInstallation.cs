using System.Globalization;
using BHS.Shared;

namespace BHS.Revit.Launch;

/// <summary>
/// One Revit found on this machine, and the places its add-ins live.
/// </summary>
/// <remarks>
/// Discovered by looking rather than by listing: which releases are installed changes, and a list
/// in the source would be wrong the first time one is added or removed.
/// </remarks>
public sealed class RevitInstallation
{
    private RevitInstallation(RevitRelease release, string executablePath)
    {
        Release = release;
        ExecutablePath = executablePath;
        InstallDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

        var year = release.Year.ToString(CultureInfo.InvariantCulture);

        AddInsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", year);

        AllUsersAddInsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "Revit", "Addins", year);

        TrustKeyPath = $@"SOFTWARE\Autodesk\Revit\Autodesk Revit {year}\CodeSigning";
    }

    public RevitRelease Release { get; }

    public string ExecutablePath { get; }

    public string InstallDirectory { get; }

    /// <summary>Per-user add-in folder for this release, under <c>%AppData%</c>.</summary>
    public string AddInsDirectory { get; }

    /// <summary>All-users add-in folder for this release, under <c>%ProgramData%</c>.</summary>
    /// <remarks>Revit reads both, so anything asking "what will load" has to look at both.</remarks>
    public string AllUsersAddInsDirectory { get; }

    /// <summary>
    /// Where this release records the add-ins the user has agreed to load, under HKCU.
    /// </summary>
    /// <remarks>
    /// One DWORD per add-in, named by its <c>AddInId</c> and set to 1, written by the "Always load"
    /// button of Revit's unsigned-add-in dialog. It is only one of the ways Revit records trust,
    /// and the weaker one: it is per add-in per release, while a trusted publisher certificate
    /// covers every add-in and every release at once. See <see cref="AddInTrust"/>.
    /// </remarks>
    public string TrustKeyPath { get; }

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

    /// <summary>The installation for one release, or null when it is not installed.</summary>
    public static RevitInstallation? Find(RevitRelease release) =>
        Discover().FirstOrDefault(installation => installation.Release == release);

    public override string ToString() => $"Revit {Release} at {ExecutablePath}";
}
