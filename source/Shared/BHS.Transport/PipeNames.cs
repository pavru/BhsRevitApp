using System.Globalization;

namespace BHS.Transport;

/// <summary>
/// The names the two sides agree on. Win-side owns one well-known name; every Revit process owns
/// a name of its own.
/// </summary>
/// <remarks>
/// Finding a companion is meant to happen by registration, not by looking: Revit-side connects to
/// the well-known name and announces where it can be called back. <see cref="Enumerate"/> exists
/// for the two cases registration cannot cover - Win-side restarted and lost its registry, and
/// diagnostics.
/// <para>
/// Enumeration is cheap and, unusually, does not accumulate stale entries: the kernel removes a
/// pipe when the last handle to it closes, so a process that died leaves nothing behind. That is
/// one of the reasons the transport is named pipes rather than loopback TCP, where a registry of
/// ports would have to be kept in a file and swept.
/// </para>
/// </remarks>
public static class PipeNames
{
    /// <summary>Every name this framework owns starts with this.</summary>
    public const string Prefix = "BHS.";

    /// <summary>
    /// The singleton Win-side name. Several Win-side processes can coexist under their instance
    /// names, but only the one that wins a <c>Global\</c> mutex additionally serves this one.
    /// </summary>
    public const string WinSide = Prefix + "WinSide";

    private const string WinSideInstancePrefix = WinSide + ".";
    private const string RevitSidePrefix = Prefix + "RevitSide.";

    /// <summary>Win-side, addressed as a particular process. For diagnostics.</summary>
    public static string WinSideInstance(int processId) => WinSideInstancePrefix + processId.ToString(Culture);

    /// <summary>One Revit process. There is no singleton form: every instance is its own endpoint.</summary>
    public static string RevitSideInstance(int processId, int revitRelease) =>
        RevitSidePrefix + processId.ToString(Culture) + "." + revitRelease.ToString(Culture);

    /// <summary>
    /// Reads back a name produced by <see cref="RevitSideInstance"/>.
    /// </summary>
    /// <remarks>
    /// Anything before the last backslash is ignored on purpose. A name can carry a security
    /// namespace in front of it - <c>ProtectedPrefix\Administrators\</c> - and while nothing here
    /// creates one today, only an administrator or LocalSystem can, and Revit runs as neither.
    /// Should Win-side ever move into a service, parsing should not be what breaks.
    /// </remarks>
    public static bool TryParseRevitSide(string? pipeName, out int processId, out int revitRelease)
    {
        processId = 0;
        revitRelease = 0;

        if (string.IsNullOrEmpty(pipeName))
            return false;

        var name = StripNamespace(pipeName!);

        if (!name.StartsWith(RevitSidePrefix, StringComparison.Ordinal))
            return false;

        var parts = name.Substring(RevitSidePrefix.Length).Split('.');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, Culture, out processId)
               && int.TryParse(parts[1], NumberStyles.None, Culture, out revitRelease);
    }

    /// <summary>True for any name this framework could have created.</summary>
    public static bool IsFrameworkPipe(string? pipeName) =>
        !string.IsNullOrEmpty(pipeName) && StripNamespace(pipeName!).StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Every framework pipe currently open on this machine. A name here means a live server; it
    /// does not mean a companion that will answer, so confirm with a short call carrying a
    /// deadline before treating one as a peer.
    /// </summary>
    public static IReadOnlyList<string> Enumerate()
    {
        string[] entries;

        try
        {
            // The pipe namespace is enumerable as a directory. It is not a real one: names may
            // appear and vanish between the listing and the connection.
            entries = Directory.GetFiles(@"\\.\pipe\");
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        var found = new List<string>();

        foreach (var entry in entries)
        {
            var name = StripNamespace(entry);
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
                found.Add(name);
        }

        return found;
    }

    private static CultureInfo Culture => CultureInfo.InvariantCulture;

    private static string StripNamespace(string pipeName)
    {
        var lastSeparator = pipeName.LastIndexOf('\\');
        return lastSeparator < 0 ? pipeName : pipeName.Substring(lastSeparator + 1);
    }
}
