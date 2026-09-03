using System.Diagnostics;
using System.Globalization;
using BHS.Revit.Launch;
using Microsoft.Win32;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// Puts the probe where Revit looks for add-ins, and takes it away again.
/// </summary>
/// <remarks>
/// Deployment is the SDK's own path rather than a copy written here: building the probe with
/// <c>RevitDeploy=Local</c> generates the manifest, lays out the vendor folder and installs it.
/// Using it means the runner exercises the publish targets instead of a second implementation
/// that could drift from them.
/// <para>
/// The build is run without a target framework, so all four are built and all four installed in
/// one pass - which is also why the runner needs no table mapping a release to a moniker.
/// </para>
/// </remarks>
internal static class ProbeInstaller
{
    public static bool Deploy(string repositoryRoot, IEnumerable<RevitInstallation> installations)
    {
        var project = Path.Combine(repositoryRoot, ProbeDeployment.ProjectPath);

        // Clear first, because deployment only ever copies.
        //
        // Found the hard way: the add-in folders still held BHS.Configuration.dll and
        // GrpcDotNetNamedPipes.dll from renames weeks old, and Microsoft.Extensions.* from the day
        // that reference was removed. Revit was loading a folder that no build had produced for a
        // long time, so every measurement about "what the probe carries" was taken from bin while
        // something else ran. Worse, RVTREF005 reads the build output rather than the deployed
        // folder, so a banned assembly can outlive the rule that banned it.
        foreach (var installation in installations)
        {
            if (Directory.Exists(ProbeDirectory(installation)))
                Directory.Delete(ProbeDirectory(installation), recursive: true);
        }

        Console.WriteLine("building and installing the probe for every supported release...");

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
        {
            Trust(installation);
            WriteSettings(installation);
        }

        Console.WriteLine("recorded the probe as trusted for: " + string.Join(", ", installations.Select(one => one.Release)));
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

            if (Directory.Exists(ProbeDirectory(installation)))
            {
                Directory.Delete(ProbeDirectory(installation), recursive: true);
                removed = true;
            }

            if (Untrust(installation))
                removed = true;

            Console.WriteLine($"  Revit {installation.Release}: {(removed ? "removed" : "nothing to remove")}");
        }
    }

    /// <summary>
    /// Lays two settings files into the probe's own folder, so the sweep can prove they are read.
    /// </summary>
    /// <remarks>
    /// The product layer only, and inside the folder <c>--undeploy</c> removes. Writing into
    /// <c>%ProgramData%\BHS</c> or <c>%AppData%\BHS</c> would prove the same thing and leave a
    /// file behind in a place a person is going to want to edit themselves.
    /// <para>
    /// Two files rather than one, because one would only show that a file is read. The pair shows
    /// which one wins: the common file names the layer, the release-specific file overrules it, and
    /// what comes back has to be the release the probe is running under.
    /// </para>
    /// </remarks>
    public static void WriteSettings(RevitInstallation installation)
    {
        var directory = ProbeLibDirectory(installation);

        if (!Directory.Exists(directory))
            return;

        var release = installation.Release.Year.ToString(CultureInfo.InvariantCulture);

        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
            {
              // written by the probe runner; removed with --undeploy
              "Probe": { "Marker": "product", "Layer": "common" }
            }
            """);

        File.WriteAllText(Path.Combine(directory, "appsettings.revit" + release + ".json"), $$"""
            {
              "Probe": { "Layer": "revit{{release}}" }
            }
            """);
    }

    /// <summary>
    /// Records that this user agrees to load the probe, the way the dialog's "Always load" does.
    /// </summary>
    /// <remarks>
    /// Necessary rather than convenient. An add-in without an Authenticode signature makes Revit
    /// raise a modal dialog before loading it, whose default answer is "do not load" - measured on
    /// the first sweep, which sat in front of that dialog until the deadline and reported nothing.
    /// Nothing outside the process can answer a Revit dialog, so a headless run needs the answer
    /// recorded in advance.
    /// <para>
    /// Deliberately part of installing rather than of every sweep: it is a trust decision, it
    /// belongs to the step that asked for one, and <c>--undeploy</c> takes it back. The real
    /// remedy for shipped code is a signature, and then this key is not needed at all.
    /// </para>
    /// </remarks>
    public static void Trust(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.CreateSubKey(installation.TrustKeyPath, writable: true);
        key?.SetValue(ProbeDeployment.AddInId, 1, RegistryValueKind.DWord);
    }

    public static bool Untrust(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath, writable: true);

        if (key?.GetValue(ProbeDeployment.AddInId) is null)
            return false;

        key.DeleteValue(ProbeDeployment.AddInId, throwOnMissingValue: false);
        return true;
    }

    public static bool IsTrusted(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath);
        return key?.GetValue(ProbeDeployment.AddInId) is int allowed && allowed == 1;
    }

    /// <summary>Where the SDK puts the probe manifest for one release.</summary>
    /// <remarks>
    /// Worked out the same way the SDK works it out, rather than read back from it. A wrong value
    /// fails visibly at the deployment check instead of quietly at launch.
    /// </remarks>
    public static string ManifestPath(RevitInstallation installation) =>
        Path.Combine(installation.AddInsDirectory, ProbeDeployment.ManifestFileName);

    /// <summary>The folder the whole deployment lives under, and the one that is removed.</summary>
    public static string ProbeDirectory(RevitInstallation installation) =>
        Path.Combine(installation.AddInsDirectory, ProbeDeployment.VendorId, ProbeDeployment.AddInName);

    /// <summary>
    /// Where the assembly itself lands, one level further down.
    /// </summary>
    /// <remarks>
    /// The distinction cost a sweep. The manifest names
    /// <c>BimHouseSoftware\BHS.Revit.Probe\Lib\BHS.Revit.Probe.dll</c>, so anything meant to sit
    /// beside the add-in - a settings file, above all - belongs in <c>Lib</c> and not in the folder
    /// above it. Written one level too high, the files existed, were never read, and the checks
    /// failed for a reason that looked like the reader.
    /// </remarks>
    public static string ProbeLibDirectory(RevitInstallation installation) =>
        Path.Combine(ProbeDirectory(installation), ProbeDeployment.LibDirectoryName);

    /// <summary>True when both halves of an installation are present.</summary>
    public static bool IsDeployed(RevitInstallation installation) =>
        File.Exists(ManifestPath(installation)) && Directory.Exists(ProbeLibDirectory(installation));

    /// <summary>The repository this build came from, or null when it was copied elsewhere.</summary>
    public static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BhsRevitApp.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
