namespace BHS.Revit.Probe;

/// <summary>
/// Which copy of each interesting assembly this AppDomain actually ended up with.
/// </summary>
/// <remarks>
/// The whole reason the probe exists. Revit 2025 and later ship their own <c>Grpc.Core.Api</c> and
/// <c>Google.Protobuf</c> and load them long before any add-in, and every release ships
/// <c>Newtonsoft.Json</c> and some ship <c>Microsoft.Extensions.*</c>. Whoever loads first wins,
/// and on Revit 2024 there is no isolation to lose the argument in: every add-in of every vendor
/// shares one AppDomain.
/// <para>
/// Static analysis has already compared the surfaces; RefCheck does that on every build. What it
/// cannot say is which file is open at run time, and that is a question with a one-word answer
/// only once something has asked the running process.
/// </para>
/// </remarks>
internal static class LoadedAssemblies
{
    /// <summary>
    /// Names worth reporting. Anything whose simple name starts with one of these: the assemblies
    /// Revit is known to ship a copy of, the polyfills a .NET Framework build drags in, and our
    /// own, so the report shows both sides of every collision.
    /// </summary>
    private static readonly string[] Interesting =
    {
        "BHS.",
        "Google.Protobuf",
        "Grpc.",
        "GrpcDotNetNamedPipes",
        "Microsoft.Extensions.",
        "Newtonsoft.Json",
        "System.Buffers",
        "System.Collections.Immutable",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Threading.Tasks.Extensions",
    };

    /// <summary>
    /// One entry per interesting assembly: <c>assembly:&lt;name&gt;</c> mapped to version and path.
    /// </summary>
    /// <remarks>
    /// Version and location in one value rather than two keys. The pair is what answers the
    /// question - a version alone cannot distinguish our copy from Revit's when the two carry the
    /// same <c>AssemblyVersion</c>, which is exactly the Newtonsoft case.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Report()
    {
        var report = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            total++;

            var name = assembly.GetName();
            var simpleName = name.Name ?? string.Empty;

            if (!IsInteresting(simpleName))
                continue;

            // A dynamic assembly has no file, and asking for its location throws rather than
            // returning empty on .NET Framework.
            string location;
            try
            {
                location = assembly.IsDynamic ? "(dynamic)" : assembly.Location;
            }
            catch (NotSupportedException)
            {
                location = "(no file)";
            }

            report["assembly:" + simpleName] = name.Version + " | " + location;
        }

        report["assembly:count"] = total.ToString();
        return report;
    }

    private static bool IsInteresting(string simpleName)
    {
        foreach (var prefix in Interesting)
        {
            if (simpleName.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
