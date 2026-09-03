using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using BHS.Transport;

namespace BHS.Revit.Probe;

/// <summary>
/// Everything this Revit instance can say about itself, captured once on the API thread.
/// </summary>
/// <remarks>
/// Captured rather than read on demand. The Revit API is single-threaded and every question here
/// arrives on a pool thread of the pipe server, so the values are taken while <c>OnStartup</c>
/// still holds the API thread and are plain strings from then on.
/// </remarks>
internal sealed class ProbeFacts
{
    private readonly object _gate = new();
    private string _documentTitle = string.Empty;
    private string _documentPath = string.Empty;

    public ProbeFacts(ControlledApplication application)
    {
        InstanceId = Guid.NewGuid().ToString("N");
        ProcessId = Process.GetCurrentProcess().Id;
        ApiThreadId = Environment.CurrentManagedThreadId;

        VersionNumber = application.VersionNumber ?? string.Empty;
        VersionBuild = application.VersionBuild ?? string.Empty;
        VersionName = application.VersionName ?? string.Empty;
        Language = application.Language.ToString();

        Release = int.TryParse(VersionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var release)
            ? release
            : 0;

        // Where Revit itself lives, and where we were loaded from. The pair is what turns an
        // assembly's path into an answer: our copy or Revit's.
        InstallDirectory = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;

        var probe = typeof(ProbeFacts).Assembly;
        AddInAssembly = SafeLocation(probe);
        AddInDirectory = string.IsNullOrEmpty(AddInAssembly) ? string.Empty : Path.GetDirectoryName(AddInAssembly) ?? string.Empty;

        CorrelationToken = global::BHS.Transport.CorrelationToken.FromEnvironment();
        PipeName = PipeNames.RevitSideInstance(ProcessId, Release);
    }

    public string InstanceId { get; }

    public int ProcessId { get; }

    /// <summary>The thread <c>OnStartup</c> ran on, which is the only thread the API may be used from.</summary>
    public int ApiThreadId { get; }

    public int Release { get; }

    public string VersionNumber { get; }

    public string VersionBuild { get; }

    public string VersionName { get; }

    public string Language { get; }

    public string InstallDirectory { get; }

    public string AddInAssembly { get; }

    public string AddInDirectory { get; }

    /// <summary>Null when a person started this Revit rather than the runner. Not an error.</summary>
    public string? CorrelationToken { get; }

    public string PipeName { get; }

    public void SetDocument(string title, string pathName)
    {
        lock (_gate)
        {
            _documentTitle = title ?? string.Empty;
            _documentPath = pathName ?? string.Empty;
        }
    }

    /// <summary>What this instance publishes as configuration.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Revit:Release"] = Release.ToString(CultureInfo.InvariantCulture),
            ["Revit:VersionNumber"] = VersionNumber,
            ["Revit:VersionBuild"] = VersionBuild,
            ["Revit:VersionName"] = VersionName,
            ["Revit:Language"] = Language,
            ["Revit:InstallDirectory"] = InstallDirectory,
            ["Process:Id"] = ProcessId.ToString(CultureInfo.InvariantCulture),
            ["Process:ApiThread"] = ApiThreadId.ToString(CultureInfo.InvariantCulture),
            ["AddIn:Directory"] = AddInDirectory,
            ["AddIn:Assembly"] = AddInAssembly,
            ["AddIn:Log"] = ProbeLog.Path,
            ["Instance:Id"] = InstanceId,
        };

        lock (_gate)
        {
            values["Document:Title"] = _documentTitle;
            values["Document:PathName"] = _documentPath;
        }

        // The token itself is deliberately not published. It is how the runner recognises this
        // process, and a secret that travels twice is one that can be observed twice.
        values["Instance:StartedByRunner"] = (CorrelationToken is not null).ToString();

        return values;
    }

    /// <summary>Diagnostics that answer questions about the host rather than about a model.</summary>
    public IReadOnlyDictionary<string, string> Context() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["appdomain:id"] = AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture),
        ["appdomain:name"] = AppDomain.CurrentDomain.FriendlyName,
        ["appdomain:base"] = InstallDirectory,
        ["addin:directory"] = AddInDirectory,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["revit:build"] = VersionBuild,
        ["revit:name"] = VersionName,
        ["revit:language"] = Language,
    };

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? string.Empty : assembly.Location;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }
}
