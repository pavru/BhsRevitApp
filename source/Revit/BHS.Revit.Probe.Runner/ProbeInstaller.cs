using System.Diagnostics;
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
            Trust(installation);

        Console.WriteLine("recorded the probe as trusted for: " + string.Join(", ", installations.Select(one => one.Release)));
        return true;
    }

    public static void Undeploy(IEnumerable<RevitInstallation> installations)
    {
        foreach (var installation in installations)
        {
            var removed = false;

            if (File.Exists(installation.ManifestPath))
            {
                File.Delete(installation.ManifestPath);
                removed = true;
            }

            if (Directory.Exists(installation.ProbeDirectory))
            {
                Directory.Delete(installation.ProbeDirectory, recursive: true);
                removed = true;
            }

            if (Untrust(installation))
                removed = true;

            Console.WriteLine($"  Revit {installation.Release}: {(removed ? "removed" : "nothing to remove")}");
        }
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
