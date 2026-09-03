using System.Reflection;

namespace BHS.Revit.Probe;

/// <summary>
/// Binding redirects for an assembly that has nowhere to put an app.config.
/// </summary>
/// <remarks>
/// On .NET Framework a reference to version 4.0.2.0 does not bind to 4.0.5.0; an application
/// papers over that with <c>bindingRedirect</c> entries the SDK generates into its
/// <c>.exe.config</c>. A Revit add-in has no config of its own - the host's governs - and
/// <c>Revit.exe.config</c> carries redirects only for what Revit itself ships. So a Revit-side
/// assembly on net48 has to supply its own, and this is the way to do it.
/// <para>
/// Measured, not anticipated. The same transport is green on net48 as a console application and
/// failed inside Revit 2024 with <c>MissingMethodException: Method not found:
/// 'System.Buffers.IBufferWriter`1&lt;Byte&gt; Grpc.Core.SerializationContext.GetBufferWriter()'</c>.
/// The cause was two copies of the polyfills: the console build resolved System.Memory 4.0.5.0
/// because it also carries Microsoft.Extensions.*, while the add-in resolved 4.0.2.0, and
/// <c>IBufferWriter&lt;byte&gt;</c> from one is not <c>IBufferWriter&lt;byte&gt;</c> from the other.
/// </para>
/// <para>
/// Resolving by simple name is exactly what a redirect does: whatever version anyone asks for,
/// they get the one file we shipped, so every reference unifies on one identity. It only runs when
/// normal binding has already failed, so it cannot take an assembly away from anybody.
/// </para>
/// <para>
/// This belongs in the framework's host layer rather than in a probe, and should move there when
/// one exists. It is here because the probe is the first Revit-side assembly to carry a dependency
/// at all, and therefore the first to need it.
/// </para>
/// </remarks>
internal static class AssemblyRedirect
{
    private static string _directory = string.Empty;
    private static bool _installed;

    /// <summary>
    /// Points assembly resolution at the folder this add-in was deployed to.
    /// </summary>
    /// <remarks>
    /// Must run before anything touches a type from a redirected assembly. The JIT resolves every
    /// type a method mentions when it compiles that method, not when execution reaches the line,
    /// so the call has to be in a method of its own that mentions none of them.
    /// </remarks>
    public static void Install()
    {
        if (_installed)
            return;

        _directory = Path.GetDirectoryName(typeof(AssemblyRedirect).Assembly.Location) ?? string.Empty;

        if (_directory.Length == 0)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        _installed = true;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string simpleName;

        try
        {
            simpleName = new AssemblyName(args.Name).Name ?? string.Empty;
        }
        catch (FileLoadException)
        {
            return null;
        }

        if (simpleName.Length == 0)
            return null;

        // Our folder first, then Revit's. The second is what makes a polyfill work: Revit 2024
        // ships System.Memory 4.0.1.1 in its own directory, which is the application base, so a
        // reference to that exact version binds there and never reaches this handler. A reference
        // to any other version has to be steered onto the same file, or the process ends up with
        // two System.Buffers.IBufferWriter<byte> types and every signature mentioning one stops
        // matching.
        var candidate = Path.Combine(_directory, simpleName + ".dll");

        if (!File.Exists(candidate))
            candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, simpleName + ".dll");

        if (!File.Exists(candidate))
        {
            // Resource assemblies come through here constantly, asking for names like
            // "Foo.resources". Nothing of ours answers to those.
            return null;
        }

        try
        {
            var resolved = Assembly.LoadFrom(candidate);
            ProbeLog.Write($"redirect: {args.Name} -> {resolved.GetName().Version}");
            return resolved;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }
}
