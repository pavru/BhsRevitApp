using System.Diagnostics;
using System.Globalization;
using BHS.Revit.Launch;
using Microsoft.Win32;

namespace BHS.Revit.Probe.Runner;

/// <summary>Where the edition lands, worked out the same way the SDK works it out.</summary>
internal static class EditionDeployment
{
    public const string VendorId = "BimHouseSoftware";
    public const string PackageName = "BHS.FullEdition";
    public const string ManifestFileName = PackageName + ".addin";
    public const string LibDirectoryName = "Lib";

    /// <summary>The id in <c>BHS.FullEdition.addin</c>, which is what Revit files trust by.</summary>
    public const string AddInId = "3f8b1d64-9c27-4a5e-8d13-6e0a72c4f5b1";

    public const string ProjectPath = @"source\BHS.FullEdition\BHS.FullEdition.csproj";
}

/// <summary>
/// Installs the edition beside the probe, from the same tree and in the same breath.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because two deployments made at different moments drift, and the drift is not
/// visible until something fails oddly.</b> Measured: the probe was installed twenty minutes before
/// a method was added to <c>BHS.Settings</c>, the edition after; both copies called themselves
/// <c>0.1.0.0</c>, the older one won the simple name in Revit's shared AppDomain, and the first
/// press of the first command died on a missing method.
/// </para>
/// <para>
/// The runner already rebuilds and reinstalls the probe from the current tree on every
/// <c>--deploy</c>. This makes the edition part of that same act, so "both came from one build"
/// stops being a thing to remember and becomes a property of the command.
/// </para>
/// <para>
/// <b>Not on by default.</b> The probe is an instrument and the edition is a product; a sweep is
/// about the framework, and installing a product into somebody's Revit is not a side effect a
/// measurement should have. <c>--undeploy</c> removes both, because leaving one behind is the drift
/// this is here to prevent.
/// </para>
/// </remarks>
internal static class EditionInstaller
{
    public static bool Deploy(string repositoryRoot, IReadOnlyList<RevitInstallation> installations)
    {
        var project = Path.Combine(repositoryRoot, EditionDeployment.ProjectPath);

        // Cleared first for the same reason the probe's folder is: deployment only ever copies, so
        // a folder that is never cleared accumulates assemblies no build has produced for weeks.
        foreach (var installation in installations)
        {
            if (Directory.Exists(EditionDirectory(installation)))
                Directory.Delete(EditionDirectory(installation), recursive: true);
        }

        Console.WriteLine("building and installing the edition for every supported release...");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("-p:RevitDeploy=Local");

        using var build = Process.Start(startInfo);

        if (build is null)
            return false;

        build.WaitForExit();

        if (build.ExitCode != 0)
            return false;

        foreach (var installation in installations)
            Trust(installation);

        Console.WriteLine("recorded the edition as trusted for: "
                          + string.Join(", ", installations.Select(one => one.Release)));

        return true;
    }

    public static void Undeploy(IEnumerable<RevitInstallation> installations)
    {
        foreach (var installation in installations)
        {
            var removed = false;

            if (File.Exists(ManifestPath(installation)))
            {
                File.Delete(ManifestPath(installation));
                removed = true;
            }

            if (Directory.Exists(EditionDirectory(installation)))
            {
                Directory.Delete(EditionDirectory(installation), recursive: true);
                removed = true;
            }

            if (Untrust(installation))
                removed = true;

            Console.WriteLine($"  Revit {installation.Release}: edition {(removed ? "removed" : "not installed")}");
        }
    }

    /// <summary>
    /// Says whether the two add-ins now agree about the framework they share.
    /// </summary>
    /// <remarks>
    /// <b>The runtime check, brought forward to the one moment when it is cheap to act on.</b>
    /// <c>FrameworkAssemblyCheck</c> reports a mismatch from inside Revit, by which point somebody
    /// is already debugging; here the answer arrives while both folders are still warm and the fix
    /// is to run the command again. It compares only what both actually carry, so an assembly one
    /// of them does not ship is not a finding.
    /// </remarks>
    public static void ReportSharedAssemblies(IReadOnlyList<RevitInstallation> installations)
    {
        foreach (var installation in installations)
        {
            var probe = ProbeInstaller.ProbeLibDirectory(installation);
            var edition = Path.Combine(EditionDirectory(installation), EditionDeployment.LibDirectoryName);

            if (!Directory.Exists(probe) || !Directory.Exists(edition))
                continue;

            var disagreed = new List<string>();

            foreach (var path in Directory.GetFiles(edition, "BHS.*.dll"))
            {
                var name = Path.GetFileName(path);
                var beside = Path.Combine(probe, name);

                if (!File.Exists(beside))
                    continue;

                if (!string.Equals(FileVersion(path), FileVersion(beside), StringComparison.Ordinal))
                    disagreed.Add(name + " " + FileVersion(beside) + " vs " + FileVersion(path));
            }

            Console.WriteLine(disagreed.Count == 0
                ? $"  Revit {installation.Release}: probe and edition agree on every shared assembly"
                : $"  Revit {installation.Release}: THEY DISAGREE - {string.Join("; ", disagreed)}");
        }
    }

    public static void Trust(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.CreateSubKey(installation.TrustKeyPath, writable: true);
        key?.SetValue(EditionDeployment.AddInId, 1, RegistryValueKind.DWord);
    }

    public static bool Untrust(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath, writable: true);

        if (key?.GetValue(EditionDeployment.AddInId) is null)
            return false;

        key.DeleteValue(EditionDeployment.AddInId, throwOnMissingValue: false);
        return true;
    }

    public static string ManifestPath(RevitInstallation installation) =>
        Path.Combine(installation.AddInsDirectory, EditionDeployment.ManifestFileName);

    public static string EditionDirectory(RevitInstallation installation) =>
        Path.Combine(installation.AddInsDirectory, EditionDeployment.VendorId, EditionDeployment.PackageName);

    private static string FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "(none)";
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }
}
