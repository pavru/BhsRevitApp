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

    /// <summary>
    /// Says what is actually in the add-in folder for one release: version, when it was written,
    /// and whether it carries a signature.
    /// </summary>
    /// <remarks>
    /// Because a sweep that reports on the wrong build reports on nothing, and this repository has
    /// spent whole sessions on exactly that: an SDK whose extracted copy in the NuGet cache was
    /// older than its sources, and a day of builds that went out unsigned because the thumbprint
    /// had been set for one terminal window. Neither had a symptom - both would have had one line
    /// here.
    ///
    /// It never throws. A sweep must not fail because it could not describe itself.
    ///
    /// Called before the sweep rather than after a deploy, and the difference is the whole point:
    /// after a deploy the build is known to be fresh, because it was made two lines earlier. The
    /// run where this can be wrong is the ordinary one - deploy once, sweep repeatedly - and that
    /// is the run where saying nothing would leave the original problem exactly where it was.
    /// </remarks>
    public static DeployedProbe Describe(RevitInstallation installation)
    {
        try
        {
            var assembly = Path.Combine(ProbeLibDirectory(installation), ProbeDeployment.AddInName + ".dll");

            if (!File.Exists(assembly))
            {
                Console.WriteLine($"  Revit {installation.Release}: nothing at {assembly}");
                return new DeployedProbe(null, null, false);
            }

            var info = FileVersionInfo.GetVersionInfo(assembly);
            var written = File.GetLastWriteTime(assembly);
            var signed = IsSigned(assembly);

            Console.WriteLine(
                $"  Revit {installation.Release}: {ProbeDeployment.AddInName}.dll " +
                $"{info.FileVersion} built {written:yyyy-MM-dd HH:mm:ss}, {(signed ? "signed" : "UNSIGNED")}");

            return new DeployedProbe(
                info.FileVersion,
                written.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                signed);
        }
        catch (Exception error)
        {
            Console.WriteLine($"  Revit {installation.Release}: could not be described ({error.GetType().Name})");
            return new DeployedProbe(null, null, false);
        }
    }

    /// <summary>
    /// Whether the file carries an Authenticode signature, without asking whether it is trusted.
    /// </summary>
    /// <remarks>
    /// Trust is a question about the machine and is answered elsewhere; this one is about the file,
    /// and it is the half that goes missing silently when RevitSignWith is empty. Read straight out
    /// of the PE header: a signature lives in the certificate table, which is the fifth data
    /// directory, and a non-zero size there means the file was signed. The obvious API,
    /// <c>X509Certificate.CreateFromSignedFile</c>, is obsolete as of SYSLIB0057, and the
    /// replacement loads certificates rather than reading them out of an image.
    ///
    /// Never throws: a sweep must not fail because it could not describe itself.
    /// </remarks>
    private static bool IsSigned(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);

            file.Position = 0x3C;
            var peHeader = reader.ReadInt32();

            file.Position = peHeader;
            if (reader.ReadUInt32() != 0x00004550)   // the PE signature
                return false;

            file.Position = peHeader + 4 + 20;       // past the COFF header, at the optional header
            var magic = reader.ReadUInt16();

            // The directories sit at a different offset in PE32 and PE32+, because the fields
            // before them differ in width. The certificate table is the fifth of them.
            var directories = magic == 0x20B ? 112 : 96;
            file.Position = peHeader + 4 + 20 + directories + (4 * 8);

            reader.ReadUInt32();                     // address, which we do not need
            return reader.ReadUInt32() > 0;          // size: non-zero means a signature is present
        }
        catch (Exception)
        {
            return false;
        }
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
              "Probe": { "Marker": "product", "Layer": "common" },
              // On for the sweep, off everywhere else. The host reads this before subscribing to
              // Revit's progress and dialog events, and the default is off because those handlers
              // run on the API thread of every model load in the process - a cost a product should
              // not pay to be watched by nobody.
              "Diagnostics": { "Enabled": true },
              // The DB half's module reads this one, which proves the host narrowed the settings to
              // the module rather than handing it the whole store.
              "ProbeDbModule": { "Marker": "product" },
              // A project-scoped key set by the vendor, so that the sweep can prove a project
              // removes it rather than merely overriding it.
              "Model": { "Probe": { "Cleared": "set-by-the-product-layer" } }
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

        // Every id in the manifest, not just the first. The value is keyed by add-in, so one entry
        // would leave the other half asking - and a dialog nobody sees is what stops a sweep dead.
        foreach (var id in ProbeDeployment.AddInIds)
            key?.SetValue(id, 1, RegistryValueKind.DWord);
    }

    public static bool Untrust(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath, writable: true);

        if (key is null)
            return false;

        var removed = false;

        foreach (var id in ProbeDeployment.AddInIds)
        {
            if (key.GetValue(id) is null)
                continue;

            key.DeleteValue(id, throwOnMissingValue: false);
            removed = true;
        }

        return removed;
    }

    public static bool IsTrusted(RevitInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath);

        return key is not null
               && ProbeDeployment.AddInIds.All(id => key.GetValue(id) is int allowed && allowed == 1);
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
