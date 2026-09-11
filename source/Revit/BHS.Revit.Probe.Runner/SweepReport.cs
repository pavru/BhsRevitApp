using System.Globalization;
using System.Text.Json;

namespace BHS.Revit.Probe.Runner;

/// <summary>One check, as it will be read back by something that was not there when it ran.</summary>
internal sealed record CheckRecord(string Name, bool Ok);

/// <summary>What one Revit release contributed to a sweep.</summary>
internal sealed class ReleaseRecord
{
    public int Release { get; set; }

    public string? FileVersion { get; set; }

    public string? BuiltUtc { get; set; }

    public bool? Signed { get; set; }

    public List<CheckRecord> Checks { get; set; } = new();

    /// <summary>Whether this release gave up part way, so its missing checks are explained.</summary>
    /// <remarks>
    /// Recorded rather than inferred from the count, because the two questions are different: a
    /// release with few checks either stopped on purpose or lost checks nobody noticed, and the
    /// floor that guards the second must be able to tell them apart by more than arithmetic.
    /// </remarks>
    public bool Abandoned { get; set; }

    /// <summary>The measurements: how long registration took, how many assemblies were loaded.</summary>
    /// <remarks>
    /// A list, not a dictionary keyed by label, and that is a correction. Three call sites emit the
    /// same label repeatedly for different facts - one line per settings layer, one per assembly
    /// Revit shadowed - so a dictionary kept the last and dropped the rest. Measured on the first
    /// recorded sweep: Revit 2024 recorded one shadowed assembly where the log printed several.
    /// The record existed to preserve exactly that detail.
    /// </remarks>
    public List<NoteRecord> Notes { get; set; } = new();
}

/// <summary>
/// A sweep, written down so that something other than the person who ran it can check it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a record of a run, not a run.</b> Revit cannot be installed on a hosted runner - it
/// needs an interactive session and a licence - so CI never executes any of this. What CI does is
/// read the file: that it belongs to this commit, that all four releases are present, that nothing
/// failed, and that no check has disappeared since the last one. That is worth having, and it is
/// not the same thing as running Revit, which is why the disclaimer travels inside the file rather
/// than in a comment somebody has to go and find.
/// </para>
/// <para>
/// The raw console log is deliberately not what gets committed. Measured on a full sweep: 3404
/// lines, 288 KB, of which 3003 lines are somebody else's - Dynamo, the MCP server Revit 2027
/// ships, stack traces from other vendors' plugins, ports and pids that differ every run. As a
/// file under version control that is 288 KB of noise per sweep and a diff nobody can read. This
/// carries the four hundred lines that are ours, as data.
/// </para>
/// </remarks>
internal sealed class SweepReport
{
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Said in the file itself, so that it is read by whoever reads the file.</summary>
    public string Disclaimer { get; set; } =
        "Recorded on a developer machine. Revit cannot run on hosted CI, so CI verifies this record " +
        "rather than reproducing it: no automated check here has ever started a Revit.";

    public string? Commit { get; set; }

    public bool CommitClean { get; set; }

    /// <summary>
    /// The git tree hash of every directory whose content decides what a sweep would find.
    /// </summary>
    /// <remarks>
    /// This, and not the commit, is what makes a record checkable, and the difference was found the
    /// hard way: the first pull request carrying a record was squashed, which is how this repository
    /// merges - and a squash writes a NEW commit, so the one the record named was no longer in the
    /// history at all. The staleness check then failed on a record that was perfectly current.
    ///
    /// A tree hash asks the right question. Not "was this the commit" - the commit is an accident
    /// of how the work was folded together - but "is this the same content". It survives squashing,
    /// rebasing and cherry-picking, because none of them change what the files say.
    ///
    /// The commit is still recorded, for a reader who wants to go and look. It is no longer what
    /// the check depends on.
    /// </remarks>
    public Dictionary<string, string> Content { get; set; } = new();

    public string? RecordedUtc { get; set; }

    public bool WithModel { get; set; }

    public bool ShowTab { get; set; }

    /// <summary>Whether the model opened was the linked set rather than the empty one.</summary>
    /// <remarks>
    /// Part of the mode, and the verifier compares check lists only between records of the same
    /// mode. The empty model and the linked set ask the same questions but answer different numbers
    /// of them: the carrier cases stand down on the first and assert on the second, so a list taken
    /// against one would show checks "disappearing" when compared with the other.
    /// </remarks>
    public bool Linked { get; set; }

    public List<ReleaseRecord> Releases { get; set; } = new();

    public int Performed { get; set; }

    public int Failed { get; set; }

    /// <summary>
    /// How the record is written. Nulls are written out rather than omitted, deliberately.
    /// </summary>
    /// <remarks>
    /// Omitting them saves a few bytes and costs the reader its diagnostics: a consumer under
    /// PowerShell's StrictMode meets a property that is not there and dies with "the property
    /// cannot be found", instead of the sentence somebody wrote for exactly that case. Measured -
    /// removing FileVersion from a copy of a real report turned a written explanation into an
    /// opaque failure. A record whose absent fields are invisible is a record that cannot be
    /// checked for what is missing.
    /// </remarks>
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
    };

    public void Write(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
        Console.WriteLine($"sweep report written to {path}");
    }

    /// <summary>
    /// The commit the sweep ran against, and whether anything was uncommitted at the time.
    /// </summary>
    /// <remarks>
    /// Both halves matter to whoever reads the report later. The commit is what makes the record
    /// checkable at all - a report that does not say what it tested is a report about nothing. The
    /// clean flag is what stops it from claiming more than it knows: a sweep run over a dirty tree
    /// tested something that exists on one machine and nowhere else.
    /// </remarks>
    public static (string? Commit, bool Clean, Dictionary<string, string> Content) DescribeWorkingTree(
        string repositoryRoot,
        IEnumerable<string> paths)
    {
        try
        {
            var commit = Git(repositoryRoot, "rev-parse HEAD")?.Trim();

            if (commit is null)
                return (null, false, new Dictionary<string, string>());

            // The report file itself is excluded, and this is not a loophole. Sweeping twice - once
            // to look at the results, once for the record - would otherwise have the second run see
            // the first run's uncommitted report and declare the tree dirty, and CI would then
            // reject a perfectly good record with a sentence that is simply false. Nothing else is
            // excluded: the question is whether the CODE under test was committed.
            var status = Git(repositoryRoot, "status --porcelain -- . \":(exclude)evidence/sweep-report.json\"");

            var content = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                // The tree object for that directory as HEAD has it. Absent for a path that does
                // not exist, which is itself worth recording rather than silently skipping.
                var tree = Git(repositoryRoot, $"rev-parse HEAD:{path}")?.Trim();
                content[path] = string.IsNullOrEmpty(tree) ? "absent" : tree;
            }

            return (commit, string.IsNullOrWhiteSpace(status), content);
        }
        catch (Exception)
        {
            return (null, false, new Dictionary<string, string>());
        }
    }

    /// <summary>
    /// The directories whose content decides what a sweep would find.
    /// </summary>
    /// <remarks>
    /// Deliberately not everything: documentation and CI wiring change nothing about how the add-in
    /// behaves inside Revit, and a rule that made fixing a typo cost twenty minutes of Revit is a
    /// rule somebody switches off within the week. Kept in step with the same list in
    /// build/scripts/Check-SweepReport.ps1, which is the reader of what this writes.
    /// </remarks>
    public static readonly string[] RevitSidePaths =
    {
        "source/Revit",
        "source/Shared",
        "source/WinSide",
        "build/BHS.Revit.Sdk",
    };

    private static string? Git(string workingDirectory, string arguments)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (git is null)
            return null;

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return git.ExitCode == 0 ? output : null;
    }

    public static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>What is actually installed for one release, as opposed to what was just built.</summary>
internal sealed record DeployedProbe(string? FileVersion, string? BuiltUtc, bool Signed);

/// <summary>One measurement, kept in the order it was taken.</summary>
internal sealed record NoteRecord(string Name, string Value);
