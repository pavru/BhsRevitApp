using System.IO;
using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace BHS.Revit.Host;

/// <summary>
/// Finds a pane's content class by name, in the assembly the manifest names, loading it if nobody has.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one place the host loads a feature assembly itself.</b> Everywhere else Revit does,
/// from a path on a button. Here Revit calls our creator and never sees the content class's name, so
/// the host has to do what Revit does - and the only <c>#if</c> in the pane code is here, split by
/// runtime rather than by Revit year, because that is the axis the loaders differ on.
/// </para>
/// <para>
/// <b>.NET Framework (2024):</b> <c>Assembly.LoadFrom</c>, the same load Revit gives an add-in: the
/// assembly's dependencies are then found beside it. If a copy of the same identity is already loaded
/// - by a press, or by another add-in's folder - that copy is what comes back; <c>FrameworkAssemblyCheck</c>
/// already reports the second case.
/// </para>
/// <para>
/// <b>.NET (2025-2027):</b> the edition's own load context, found from the edition's assembly. Plain
/// <c>LoadFrom</c> would put the feature in the default context even when the add-in runs isolated
/// (<c>UseRevitContext=False</c>, 2026+), and its <c>IPaneContent</c> would then be a different type
/// from the host's. An assembly of that name already in the context is taken as it is, because loading a
/// second copy of a name into one context throws.
/// </para>
/// <para>
/// None of this is measured inside Revit yet. The probe's extended mode checks the result: the content
/// was created, and the cast to the host's <c>IPaneContent</c> held.
/// </para>
/// </remarks>
internal static class PaneContentLoader
{
    /// <param name="directory">The edition's folder, where the manifest was.</param>
    /// <param name="assemblyName">The simple name the manifest gives.</param>
    /// <param name="className">The full name the manifest gives.</param>
    /// <param name="anchor">The edition's own assembly, whose load context the feature belongs in.</param>
    public static Type Resolve(string directory, string assemblyName, string className, Assembly anchor)
    {
        var path = Path.Combine(directory, assemblyName + ".dll");

        if (!File.Exists(path))
            throw new FileNotFoundException($"{assemblyName}.dll is not beside the manifest that names it", path);

#if NETFRAMEWORK
        _ = anchor;
        var assembly = Assembly.LoadFrom(path);
#else
        var context = AssemblyLoadContext.GetLoadContext(anchor) ?? AssemblyLoadContext.Default;

        var assembly = context.Assemblies.FirstOrDefault(loaded =>
                           string.Equals(loaded.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                       ?? context.LoadFromAssemblyPath(path);
#endif

        return assembly.GetType(className, throwOnError: true, ignoreCase: false)!;
    }
}
