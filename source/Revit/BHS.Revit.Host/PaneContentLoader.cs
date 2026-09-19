using System.IO;
using System.Reflection;
using BHS.Logging;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace BHS.Revit.Host;

/// <summary>
/// Finds a pane's content class by name, in the assembly the manifest names, loading it - and letting it
/// find what it needs beside itself - if nobody has.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one place the host loads a feature assembly itself.</b> Everywhere else Revit does,
/// from a path on a button. Here Revit calls our creator and never sees the content class's name, so
/// the host has to do what Revit does - and the only <c>#if</c> in the pane code is here, split by
/// runtime rather than by Revit year, because that is the axis the loaders differ on.
/// </para>
/// <para>
/// <b>Loading the file is half the job: the other half is that its dependencies are then found.</b> That
/// half was missing, and it cost the cabling pane - found by hand by the owner on 2026-09-20, not by the
/// sweep. The content assembly loaded, and the first assembly it needed at <c>Create</c> did not:
/// <c>FileNotFoundException: Could not load file or assembly 'BHS.MEP.Cabling.Ui'</c>. The pane said it
/// had stopped, and every later press of that feature's commands failed the same way, in a session where
/// pressing first would have worked.
/// </para>
/// <para>
/// <b>Measured outside Revit, on .NET 8, with three assemblies in a folder that is not the application
/// base</b> (the loader alone, no Revit in it):
/// </para>
/// <list type="bullet">
/// <item><description><c>Assembly.LoadFrom(path)</c> - the dependency is found beside the file, and so is
/// its <c>ru-RU</c> satellite;</description></item>
/// <item><description><c>context.LoadFromAssemblyPath(path)</c> - the dependency is <b>not</b> found, on
/// the default context and on a context of one's own alike;</description></item>
/// <item><description>and the difference is the <b>requesting</b> assembly, not the folder and not a
/// cached failure: after the failure, <c>Assembly.LoadFrom</c> of a second file out of the same folder
/// changed nothing, while loading the missing file by any means made the very same call succeed. The
/// runtime probes beside a requester that <c>LoadFrom</c> itself brought in, and beside no other - which
/// is why it serves us without serving anybody else, and why a press that came first fixed the pane
/// while a press that came second did not.</description></item>
/// </list>
/// <para>
/// So the content assembly is loaded the way Revit loads an add-in wherever that is possible, and where
/// it is not, the host does the probing itself in a context that holds nothing but this add-in:
/// </para>
/// <list type="bullet">
/// <item><description><b>.NET Framework (2024)</b> and <b>.NET with the add-in in the default context</b>
/// - <c>Assembly.LoadFrom</c>. The runtime's own handler answers only for assemblies it tracked, so we
/// hand our copies to no other vendor; that rule is the reason the probe's own <c>AssemblyResolve</c> was
/// taken out, and it holds here.</description></item>
/// <item><description><b>.NET with the add-in isolated</b> (<c>UseRevitContext=False</c>, 2026+) - the
/// edition's own context, plus a <c>Resolving</c> handler on it that probes the edition's folder. Plain
/// <c>LoadFrom</c> would put the feature in the default context and its <c>IPaneContent</c> would be a
/// different type from the host's. The handler answers only binds that start in that context, and that
/// context holds this add-in and nothing else. <b>Not measured inside Revit:</b> nothing in the tree runs
/// isolated today, and whether Revit's own context already probes beside the add-in is unknown - if it
/// does, this handler is never called, which is the same outcome.</description></item>
/// </list>
/// <para>
/// <b>Satellites need nothing from us</b> - measured in the same experiment, on both kinds of context: a
/// <c>.resources</c> assembly is found in <c>&lt;culture&gt;</c> beside its parent, by the runtime, once
/// the parent is loaded from its real path.
/// </para>
/// </remarks>
internal static class PaneContentLoader
{
    /// <param name="directory">The edition's folder, where the manifest was.</param>
    /// <param name="assemblyName">The simple name the manifest gives.</param>
    /// <param name="className">The full name the manifest gives.</param>
    /// <param name="anchor">The edition's own assembly, whose load context the feature belongs in.</param>
    /// <param name="log">Says how the assembly was found: the line this defect was diagnosed without.</param>
    public static Type Resolve(string directory, string assemblyName, string className, Assembly anchor, ILog log)
    {
        var path = Path.Combine(directory, assemblyName + ".dll");

        if (!File.Exists(path))
            throw new FileNotFoundException($"{assemblyName}.dll is not beside the manifest that names it", path);

        Assembly assembly;
        string how;

#if NETFRAMEWORK
        _ = anchor;
        assembly = Assembly.LoadFrom(path);
        how = "LoadFrom";
#else
        var context = AssemblyLoadContext.GetLoadContext(anchor) ?? AssemblyLoadContext.Default;

        // A copy already in the context is that copy: loading a second one of the same name into one
        // context throws, and a press may well have brought it in before anybody opened the pane.
        var already = Loaded(context, assemblyName);

        if (already is not null)
        {
            assembly = already;
            how = "already in " + Name(context);
        }
        else if (ReferenceEquals(context, AssemblyLoadContext.Default))
        {
            assembly = Assembly.LoadFrom(path);
            how = "LoadFrom into the default context";
        }
        else
        {
            // Before the load, not after: the dependency is bound while the content's own code runs.
            ProbeBeside(context, directory);
            assembly = context.LoadFromAssemblyPath(path);
            how = "into " + Name(context) + ", which now probes " + directory;
        }
#endif

        log.Info("panes: {0} resolved by {1} from {2}", assemblyName, how, assembly.Location);

        return assembly.GetType(className, throwOnError: true, ignoreCase: false)!;
    }

#if !NETFRAMEWORK
    private static readonly object Gate = new();

    // One entry per context we had to probe for, which today is at most one per isolated add-in. The
    // contexts live as long as the process does, so holding them here costs nothing and loses nothing.
    private static readonly Dictionary<AssemblyLoadContext, HashSet<string>> Probed = new();

    private static void ProbeBeside(AssemblyLoadContext context, string directory)
    {
        lock (Gate)
        {
            if (!Probed.TryGetValue(context, out var directories))
            {
                directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Probed.Add(context, directories);
                context.Resolving += Resolving;
            }

            directories.Add(directory);
        }
    }

    /// <summary>Raised only after the context and the default one have both failed, and only in that context.</summary>
    private static Assembly? Resolving(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not { Length: > 0 } simple)
            return null;

        string[] directories;

        lock (Gate)
        {
            directories = Probed.TryGetValue(context, out var registered)
                ? registered.ToArray()
                : Array.Empty<string>();
        }

        foreach (var directory in directories)
        {
            var beside = Path.Combine(directory, simple + ".dll");

            if (!File.Exists(beside))
                continue;

            // Asked again, because another Resolving handler of ours may have brought it in meanwhile.
            return Loaded(context, simple) ?? context.LoadFromAssemblyPath(beside);
        }

        return null;
    }

    private static Assembly? Loaded(AssemblyLoadContext context, string simpleName) =>
        context.Assemblies.FirstOrDefault(loaded =>
            string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    private static string Name(AssemblyLoadContext context) =>
        context.Name is { Length: > 0 } name ? "context " + name : "the default context";
#endif
}
