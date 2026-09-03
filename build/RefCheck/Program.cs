using BimHouse.RefCheck;

return Cli.Run(args);

internal static class Cli
{
    private const string MissingTypeCode = "RVTREF001";
    private const string MissingMemberCode = "RVTREF002";
    private const string MissingOverloadCode = "RVTREF003";
    private const string UnwatchedCode = "RVTREF004";
    private const string DeniedCode = "RVTREF005";

    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "collect" => Collect(args[1..]),
                "check" => Check(args[1..]),
                _ => Usage()
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"RefCheck : error : {exception.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            RefCheck - verifies that assemblies shipped in a Revit add-in folder only use
            members that exist in the copies Revit itself loads.

              RefCheck collect --revit-dir <dir> --revit-version <version> --out <file>
                               --watchlist <file> [--host-vendor <marker>]

                  Records every managed assembly a Revit installation ships. The member
                  surface is stored only for assemblies named in the watchlist; everything
                  else is listed by identity alone. Assemblies whose AssemblyCompanyAttribute
                  contains <marker> (default "Autodesk") are never indexed - those are
                  referenced through the Revit API packages, which repackage these same files.

              RefCheck check --baseline <file> --input <file-or-directory> [--input ...]

                  Verifies every managed assembly under <input> against the baseline.
                  Exit code 0 when clean, 1 when findings, 2 on usage or I/O errors.
            """);
        return 2;
    }

    private static int Collect(string[] args)
    {
        var options = Options.Parse(args);
        var revitDirectory = options.Required("revit-dir");
        var revitVersion = options.Required("revit-version");
        var output = options.Required("out");
        var watchlist = Watchlist.Load(options.Required("watchlist"));
        var hostVendor = options.Value("host-vendor") ?? "Autodesk";

        if (!Directory.Exists(revitDirectory))
            throw new IOException($"Revit directory '{revitDirectory}' does not exist.");

        var baseline = Baseline.Collect(revitDirectory, revitVersion, hostVendor, watchlist.ToSet());
        baseline.Save(output);

        var indexed = baseline.Assemblies.Count(assembly => assembly.SurfaceIndexed);
        var members = baseline.Assemblies.Sum(assembly => assembly.Members.Count);
        Console.WriteLine(
            $"Revit {revitVersion}: {baseline.Assemblies.Count} managed assemblies shipped, " +
            $"{indexed} surface-indexed, {members} members -> {output}");
        return 0;
    }

    private static int Check(string[] args)
    {
        var options = Options.Parse(args);
        var baseline = Baseline.Load(options.Required("baseline"));
        var inputs = options.Values("input");
        if (inputs.Count == 0) throw new ArgumentException("At least one --input is required.");

        // Optional so that `check` stays usable by hand against a folder, without the repository
        // around it. The build always passes it.
        var watchlistPath = options.Value("watchlist");
        var watchlist = watchlistPath is null ? null : Watchlist.Load(watchlistPath);

        var findings = new List<Finding>();
        var denied = new List<string>();
        var unwatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedFiles = 0;

        foreach (var file in ExpandInputs(inputs))
        {
            // Before anything is read out of it. This question is about the file being there at
            // all, so a name is enough and a version is beside the point: the simple name belongs
            // to whoever loaded it first, whatever version that was.
            if (watchlist is not null && watchlist.IsDenied(Path.GetFileNameWithoutExtension(file)))
                denied.Add(file);

            IReadOnlyList<MemberUse> uses;
            IReadOnlyDictionary<string, string> referencedVersions;
            try
            {
                uses = AssemblyIndex.ReadReferences(file, out referencedVersions);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            if (uses.Count == 0) continue;
            checkedFiles++;

            foreach (var use in uses)
            {
                if (!baseline.ByName.TryGetValue(use.Assembly, out var shipped)) continue;

                // A facade defines nothing; the reference resolves through its type forwards,
                // never to the file sitting in the Revit folder. Nothing to conflict with.
                if (shipped.TypeForwarderOnly) continue;

                if (!shipped.SurfaceIndexed)
                {
                    // Revit ships this assembly too, but its surface was never indexed,
                    // so nothing here can be verified. Say so rather than pass silently.
                    if (!shipped.Vendor.Contains(baseline.HostVendorMarker, StringComparison.OrdinalIgnoreCase))
                        unwatched.Add(shipped.Name);
                    continue;
                }

                referencedVersions.TryGetValue(use.Assembly, out var ourVersion);

                if (!shipped.TypeSet.Contains(use.Type) && !shipped.MemberNameSet.Contains(use.NameKey))
                {
                    findings.Add(new Finding(MissingTypeCode, file, use, shipped, ourVersion ?? "unknown",
                        $"type '{use.Type}' does not exist there"));
                    continue;
                }

                if (shipped.MemberSet.Contains(use.Key)) continue;

                if (shipped.MemberNameSet.Contains(use.NameKey))
                {
                    var available = shipped.Members
                        .Where(member => member.StartsWith(use.NameKey + "/", StringComparison.Ordinal))
                        .Select(member => member[(member.LastIndexOf('/') + 1)..]);
                    findings.Add(new Finding(MissingOverloadCode, file, use, shipped, ourVersion ?? "unknown",
                        $"'{use.Member}' exists there but only with [{string.Join(", ", available)}] argument(s)"));
                }
                else
                {
                    findings.Add(new Finding(MissingMemberCode, file, use, shipped, ourVersion ?? "unknown",
                        $"member '{use.Member}' does not exist on that type there"));
                }
            }
        }

        Report(baseline, findings, unwatched, denied, checkedFiles);
        return findings.Count == 0 && denied.Count == 0 ? 0 : 1;
    }

    private static void Report(
        Baseline baseline,
        List<Finding> findings,
        SortedSet<string> unwatched,
        List<string> denied,
        int checkedFiles)
    {
        foreach (var file in denied)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Console.Error.WriteLine(
                $"{file} : error {DeniedCode}: a Revit-side build must not ship '{name}'. " +
                "Assemblies on the denied list are ones other vendors ship too, and inside Revit " +
                "the simple name belongs to whoever loaded it first - a request for a higher " +
                "version then fails outright rather than binding low. Measured twice: System.Memory " +
                "on Revit 2024 and Microsoft.Extensions.Configuration on Revit 2026, where the " +
                "add-in did not load at all. Move whatever needs it to a Win-side assembly; the " +
                "list is in build/RefCheck/baselines/watchlist.json.");
        }

        foreach (var name in unwatched)
        {
            var shipped = baseline.ByName[name];
            Console.WriteLine(
                $"RefCheck : warning {UnwatchedCode}: your build references '{name}', which Revit " +
                $"{baseline.RevitVersion} also ships (file {Or(shipped.FileVersion, "?")}) but which is not on the " +
                $"watchlist, so it was not verified. Add \"{name}\" to build/RefCheck/baselines/watchlist.json " +
                "and re-collect the baselines.");
        }

        if (findings.Count == 0 && denied.Count == 0)
        {
            Console.WriteLine(
                $"RefCheck: {checkedFiles} assemblies checked against Revit {baseline.RevitVersion}, no conflicts.");
            return;
        }

        if (findings.Count == 0)
            return;

        foreach (var finding in findings.DistinctBy(f => (f.File, f.Use.Key, f.Code)))
        {
            Console.Error.WriteLine(
                $"{finding.File} : error {finding.Code}: " +
                $"'{Path.GetFileName(finding.File)}' uses {finding.Use} from '{finding.Shipped.Name}' " +
                $"{finding.OurVersion}. Revit {baseline.RevitVersion} ships its own '{finding.Shipped.Name}' " +
                $"(file {Or(finding.Shipped.FileVersion, "?")}, assembly {Or(finding.Shipped.AssemblyVersion, "?")}) " +
                $"and that copy is the one that loads: {finding.Detail}.");
        }

        // Plain text, not a canonical error line: the per-finding errors above already fail
        // the build, and the MSBuild target raises one summary error of its own.
        Console.WriteLine();
        Console.WriteLine($"""
            RefCheck found {findings.Count} conflict(s) with the assemblies Revit {baseline.RevitVersion} ships.

            What this means
              Revit ships its own copies of these assemblies in
                {baseline.SourcePath}
              and loads them before any add-in loads. The copy in your add-in folder is then
              ignored for the rest of the process, so the code above compiles against your
              version and executes against Revit's. Each finding is a MissingMethodException
              or TypeLoadException waiting for the first call.

            How to fix, in order of preference
              1. Stop using the member. Usually there is an equivalent that Revit's version has.
              2. Drop the dependency from the Revit-side assembly and keep it Win-side only.
              3. On Revit 2026 and newer, isolate the add-in: UseRevitContext=False plus
                 ContextName in the .addin manifest gives it its own AssemblyLoadContext,
                 and your copy wins. This does not exist for 2024 or 2025.

            If Revit itself changed
              Re-collect the baseline and review the diff:
                dotnet build/RefCheck/RefCheck.csproj -c Release
                dotnet <output>/RefCheck.dll collect --revit-dir "{baseline.SourcePath}" \
                    --revit-version {baseline.RevitVersion} \
                    --out build/RefCheck/baselines/revit-{baseline.RevitVersion}.json

            To turn this check off for one project: <RevitReferenceCheck>false</RevitReferenceCheck>
            """);
    }

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static IEnumerable<string> ExpandInputs(IReadOnlyList<string> inputs)
    {
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                yield return input;
            }
            else if (Directory.Exists(input))
            {
                foreach (var file in Directory.EnumerateFiles(input, "*.dll", SearchOption.TopDirectoryOnly)
                             .Concat(Directory.EnumerateFiles(input, "*.exe", SearchOption.TopDirectoryOnly))
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
            else
            {
                throw new IOException($"Input '{input}' does not exist.");
            }
        }
    }

    private sealed record Finding(
        string Code,
        string File,
        MemberUse Use,
        BaselineAssembly Shipped,
        string OurVersion,
        string Detail);

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument '{args[i]}'.");

                var name = args[i][2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Option '--{name}' needs a value.");

                if (!options._values.TryGetValue(name, out var list))
                    options._values[name] = list = [];
                list.Add(args[++i]);
            }

            return options;
        }

        public string? Value(string name) =>
            _values.TryGetValue(name, out var list) ? list[^1] : null;

        public IReadOnlyList<string> Values(string name) =>
            _values.TryGetValue(name, out var list) ? list : [];

        public string Required(string name) =>
            Value(name) ?? throw new ArgumentException($"Option '--{name}' is required.");
    }
}
