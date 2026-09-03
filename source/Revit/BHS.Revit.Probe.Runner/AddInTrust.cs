using System.Xml.Linq;
using Microsoft.Win32;

namespace BHS.Revit.Probe.Runner;

/// <summary>An add-in that will stop a headless start, and where it was found.</summary>
internal sealed class UntrustedAddIn
{
    public UntrustedAddIn(string name, string addInId, string manifestPath)
    {
        Name = name;
        AddInId = addInId;
        ManifestPath = manifestPath;
    }

    public string Name { get; }

    public string AddInId { get; }

    public string ManifestPath { get; }

    public override string ToString() => $"{Name} ({AddInId})";
}

/// <summary>
/// Which add-ins on this machine would make Revit stop and ask before it finishes starting.
/// </summary>
/// <remarks>
/// The probe being trusted is not enough, because the dialog is not about the probe. Revit raises
/// one modal <c>TaskDialog_Security_Unsigned_File_Loading</c> for every unsigned add-in it has not
/// been told to trust, its default answer is "do not load", and it comes up before any add-in gets
/// its <c>OnStartup</c>. From outside the process this is indistinguishable from a Revit that is
/// simply slow: the sweep waits out its whole deadline in front of a dialog nobody will answer.
/// <para>
/// It cost a run to learn, and the run said nothing useful - the probe never loaded, so it never
/// wrote a log, and the reason was in a journal line about somebody else's add-in. Asking the
/// question before starting Revit turns four minutes of silence into one line.
/// </para>
/// </remarks>
internal static class AddInTrust
{
    /// <summary>
    /// Every add-in installed for this release that is neither signed nor trusted.
    /// </summary>
    /// <remarks>
    /// Both add-in folders, because Revit reads both: the per-user one under <c>%AppData%</c> and
    /// the all-users one under <c>%ProgramData%</c>.
    /// <para>
    /// Signed is treated as quiet here, and that is a simplification measured to be incomplete.
    /// Revit has three security dialogs, not one: unsigned, signature that does not validate, and
    /// valid signature in a folder it has not been told to trust. Only the first is answered by
    /// the registry value this looks at; the other two are answered by the Windows certificate
    /// stores - a chain that validates, and the publisher in <c>TrustedPublisher</c>. Judging
    /// those from out here would mean reimplementing chain validation, so this reports what it can
    /// prove and leaves the rest to the two-line remedy: sign, and trust the publisher once.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<UntrustedAddIn> Untrusted(RevitInstallation installation)
    {
        var trusted = TrustedIds(installation);
        var found = new List<UntrustedAddIn>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in AddInDirectories(installation))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var manifest in Directory.GetFiles(directory, "*.addin"))
            {
                foreach (var addIn in Read(manifest))
                {
                    if (string.IsNullOrEmpty(addIn.AddInId) || trusted.Contains(addIn.AddInId))
                        continue;

                    if (IsSigned(addIn.AssemblyPath))
                        continue;

                    // A duplicated manifest - the same add-in in both folders - is one dialog, not
                    // two, and reporting it twice would suggest otherwise.
                    if (seen.Add(addIn.AddInId))
                        found.Add(new UntrustedAddIn(addIn.Name, addIn.AddInId, manifest));
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> AddInDirectories(RevitInstallation installation)
    {
        yield return installation.AddInsDirectory;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "Revit", "Addins", installation.Release.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static HashSet<string> TrustedIds(RevitInstallation installation)
    {
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var key = Registry.CurrentUser.OpenSubKey(installation.TrustKeyPath);
        if (key is null)
            return trusted;

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is int allowed && allowed == 1)
                trusted.Add(name);
        }

        return trusted;
    }

    private static IEnumerable<ManifestEntry> Read(string manifestPath)
    {
        XDocument document;

        try
        {
            document = XDocument.Load(manifestPath);
        }
        catch (Exception error) when (error is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(manifestPath) ?? string.Empty;

        foreach (var element in document.Descendants("AddIn"))
        {
            var id = (string?)element.Element("AddInId") ?? string.Empty;
            var assembly = (string?)element.Element("Assembly") ?? string.Empty;

            var name = (string?)element.Element("Name")
                       ?? (string?)element.Element("FullClassName")
                       ?? Path.GetFileNameWithoutExtension(manifestPath);

            yield return new ManifestEntry(
                name,
                id.Trim(),
                Path.IsPathRooted(assembly) ? assembly : Path.Combine(directory, assembly));
        }
    }

    /// <summary>
    /// True when the file carries an Authenticode signature, which is what stops Revit asking.
    /// </summary>
    /// <remarks>
    /// Read out of the PE header rather than through the certificate APIs. Whether the signature is
    /// <em>valid</em> is a different question and not this one: an expired or untrusted certificate
    /// changes what Revit says in the dialog, not whether a sweep can get past it unattended. The
    /// managed API that answers the narrow question, <c>CreateFromSignedFile</c>, is obsolete and
    /// has no replacement that reads a signature out of a file.
    /// <para>
    /// Only an embedded signature counts here. A file signed through a catalog reads as unsigned,
    /// which errs towards naming an add-in that would not in fact have stopped anything - the
    /// harmless direction for a warning to be wrong in.
    /// </para>
    /// </remarks>
    private static bool IsSigned(string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var file = File.OpenRead(assemblyPath);
            using var reader = new BinaryReader(file);

            file.Position = 0x3C;
            var peHeader = reader.ReadUInt32();

            file.Position = peHeader;
            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                return false;

            // COFF header is 20 bytes, then the optional header, whose magic says how wide it is.
            var optionalHeader = peHeader + 4 + 20;
            file.Position = optionalHeader;

            var directories = reader.ReadUInt16() switch
            {
                0x10B => optionalHeader + 96,  // PE32
                0x20B => optionalHeader + 112, // PE32+
                _ => 0u,
            };

            if (directories == 0)
                return false;

            // Data directory 4 is the certificate table. Eight bytes each: address, then size.
            file.Position = directories + (4 * 8);
            reader.ReadUInt32();

            return reader.ReadUInt32() != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private sealed class ManifestEntry
    {
        public ManifestEntry(string name, string addInId, string assemblyPath)
        {
            Name = name;
            AddInId = addInId;
            AssemblyPath = assemblyPath;
        }

        public string Name { get; }

        public string AddInId { get; }

        public string AssemblyPath { get; }
    }
}
