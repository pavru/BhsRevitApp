using BHS.Settings;
using BHS.Settings.Configuration;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Microsoft.Extensions.Configuration;

namespace BHS.Transport.Probe;

/// <summary>
/// The half of configuration that comes from disk: the file format, the layering, and the reload.
/// </summary>
/// <remarks>
/// It sits in the channel's probe rather than one of its own because the claim being tested spans
/// both halves. Each side reads its own layered files and starts with no channel at all; what the
/// companion publishes then layers on top, in the same builder, as one more source. Proving the
/// two separately would leave the only interesting part - that they compose - untested.
/// </remarks>
internal static class SettingsChecks
{
    public static async Task<int> RunAsync(string suffix)
    {
        Console.WriteLine("-- configuration from disk");

        var failures = 0;
        var root = Path.Combine(Path.GetTempPath(), "bhs-settings-" + suffix);
        Directory.CreateDirectory(root);

        try
        {
            Format(ref failures);
            Layering(ref failures, root);
            failures += await ReloadAsync(root);
            Environment(ref failures, root);
            Layout(ref failures);
            failures += await WithPeerAsync(root, suffix);
        }
        finally
        {
            TryDelete(root);
        }

        return failures;
    }

    /// <summary>The reader against the shapes the stock JSON provider accepts.</summary>
    private static void Format(ref int failures)
    {
        var read = JsonSettings.Parse("""
            {
              // what both sides share
              "Transport": {
                "WinSidePipe": "BHS.WinSide",
                "ConnectionTimeout": 5,
                "Trusted": true
              },
              "Releases": [ 2024, 2025 ],
              "Precision": 1.50,
              "Cleared": null,
              "Empty": {},
              "Escaped": "a\"b\\c\td\u00e9",   /* and a block comment */
            }
            """);

        Program.Check(ref failures, "nested object flattens with a colon", read["Transport:WinSidePipe"] == "BHS.WinSide");
        Program.Check(ref failures, "array indexes by ordinal", read["Releases:0"] == "2024" && read["Releases:1"] == "2025");
        Program.Check(ref failures, "numbers keep their literal text", read["Precision"] == "1.50");
        Program.Check(ref failures, "booleans arrive as written", read["Transport:Trusted"] == "true");
        Program.Check(ref failures, "null is stored, not dropped", read.ContainsKey("Cleared") && read["Cleared"] is null);
        Program.Check(ref failures, "an empty object clears a key", read.ContainsKey("Empty") && read["Empty"] is null);
        Program.Check(ref failures, "escapes decode, unicode included", read["Escaped"] == "a\"b\\c\tdé");
        Program.Check(ref failures, "comments and a trailing comma are accepted", read.Count == 9);
        Program.Check(ref failures, "keys are case-insensitive", read["transport:winsidepipe"] == "BHS.WinSide");

        Program.Check(ref failures, "a key set twice in one file is refused",
            Refuses("""{ "A": 1, "a": 2 }"""));
        Program.Check(ref failures, "a top level that is not an object is refused",
            Refuses("[ 1, 2 ]"));
        Program.Check(ref failures, "unquoted text is refused",
            Refuses("""{ "A": hello }"""));

        static bool Refuses(string text)
        {
            try
            {
                JsonSettings.Parse(text);
                return false;
            }
            catch (FormatException)
            {
                return true;
            }
        }
    }

    /// <summary>Later layers win, and a missing layer is not an error.</summary>
    private static void Layering(ref int failures, string root)
    {
        Write(root, "appsettings.json", """{ "Launch": { "ShutdownTimeout": 240, "NoSplash": true }, "Only": "product" }""");
        Write(root, "appsettings.revit.json", """{ "Launch": { "ShutdownTimeout": 300 } }""");
        Write(root, "appsettings.revit2026.json", """{ "Launch": { "NoSplash": false } }""");

        using var settings = LayeredSettings.Read(new SettingsOptions
        {
            Side = ProcessSide.Revit,
            Release = 2026,
            ProductDirectory = root,
            ReloadOnChange = false,
            IncludeEnvironment = false,
        });

        var launch = settings.Section("Launch");

        Program.Check(ref failures, "a later layer overrules an earlier one",
            launch.Duration("ShutdownTimeout", TimeSpan.Zero) == TimeSpan.FromSeconds(300));
        Program.Check(ref failures, "layers merge rather than replace",
            settings.Text("Only") == "product");
        Program.Check(ref failures, "the release-specific layer is the deepest of the three",
            !launch.Flag("NoSplash", true));
        Program.Check(ref failures, "the layers that do not exist are not an error",
            settings.Layers.Any(layer => !layer.Exists) && !settings.Errors.Any());
        Program.Check(ref failures, "a section addresses without its prefix",
            launch["ShutdownTimeout"] == "300" && launch["Launch:ShutdownTimeout"] is null);

        Write(root, "appsettings.revit2026.json", """{ "Launch": { "ShutdownTimeout": null } }""");
        settings.Reload();

        Program.Check(ref failures, "an explicit null clears what a shallower layer set",
            settings.Section("Launch").Duration("ShutdownTimeout", TimeSpan.FromSeconds(7)) == TimeSpan.FromSeconds(7));

        Program.Check(ref failures, "a value that cannot be read is refused, not defaulted", Throws(() =>
        {
            Write(root, "appsettings.revit2026.json", """{ "Launch": { "ShutdownTimeout": "soonish" } }""");
            settings.Reload();
            settings.Section("Launch").Duration("ShutdownTimeout", TimeSpan.Zero);
        }));

        Write(root, "appsettings.revit2026.json", "{ this is not a settings file");
        settings.Reload();

        Program.Check(ref failures, "a file that cannot be parsed is reported rather than thrown",
            settings.Errors.Any());

        File.Delete(Path.Combine(root, "appsettings.revit2026.json"));
        settings.Reload();
    }

    /// <summary>A file changed under a running process is noticed.</summary>
    private static async Task<int> ReloadAsync(string root)
    {
        var failures = 0;
        var directory = Path.Combine(root, "watched");
        Directory.CreateDirectory(directory);

        Write(directory, "appsettings.json", """{ "Launch": { "ShutdownTimeout": 240 } }""");

        using var settings = LayeredSettings.Read(new SettingsOptions
        {
            Side = ProcessSide.WinSide,
            ProductDirectory = directory,
            IncludeEnvironment = false,
        });

        var raised = 0;
        settings.Changed += (_, _) => Interlocked.Increment(ref raised);

        Write(directory, "appsettings.json", """{ "Launch": { "ShutdownTimeout": 480 } }""");

        var noticed = await Program.WaitForAsync(() => Volatile.Read(ref raised) > 0, 5000);

        Program.Check(ref failures, "a changed file is noticed without being asked", noticed);
        Program.Check(ref failures, "and the new value is the one read",
            settings.Section("Launch").Duration("ShutdownTimeout", TimeSpan.Zero) == TimeSpan.FromSeconds(480));

        return failures;
    }

    /// <summary>The environment layer, and what it is spelled like.</summary>
    private static void Environment(ref int failures, string root)
    {
        const string name = "BHS_LAUNCH__SHUTDOWNTIMEOUT";
        var directory = Path.Combine(root, "environment");
        Directory.CreateDirectory(directory);

        Write(directory, "appsettings.json", """{ "Launch": { "ShutdownTimeout": 240 } }""");

        System.Environment.SetEnvironmentVariable(name, "600");

        try
        {
            using var settings = LayeredSettings.Read(new SettingsOptions
            {
                Side = ProcessSide.WinSide,
                ProductDirectory = directory,
                ReloadOnChange = false,
            });

            Program.Check(ref failures, "a double underscore becomes a separator",
                settings.Section("Launch").Duration("ShutdownTimeout", TimeSpan.Zero) == TimeSpan.FromSeconds(600));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>The list of files each side reads, and in what order.</summary>
    private static void Layout(ref int failures)
    {
        var revit = SettingsLayout.Files(ProcessSide.Revit, 2026).Select(layer => Path.GetFileName(layer.Path)).ToList();
        var winside = SettingsLayout.Files(ProcessSide.WinSide, null).Select(layer => Path.GetFileName(layer.Path)).ToList();

        Program.Check(ref failures, "Revit-side reads three files per directory",
            revit.Count == 9 && revit[0] == "appsettings.json"
                             && revit[1] == "appsettings.revit.json"
                             && revit[2] == "appsettings.revit2026.json");

        Program.Check(ref failures, "Win-side reads two, and no release file",
            winside.Count == 6 && winside[1] == "appsettings.winside.json");

        var kinds = SettingsLayout.Files(ProcessSide.WinSide, null).Select(layer => layer.Kind).Distinct().ToList();
        Program.Check(ref failures, "product, then machine, then user",
            kinds.SequenceEqual(new[] { SettingsLayerKind.Product, SettingsLayerKind.Machine, SettingsLayerKind.User }));
    }

    /// <summary>
    /// The point of the whole arrangement: files and the companion in one builder.
    /// </summary>
    /// <remarks>
    /// The peer source is added last, so what a Revit publishes about itself overrules what a file
    /// guessed - which is the right way round, because only the Revit knows which document it has
    /// open. Everything a file can say about that is a stale guess.
    /// </remarks>
    private static async Task<int> WithPeerAsync(string root, string suffix)
    {
        var failures = 0;
        var directory = Path.Combine(root, "combined");
        Directory.CreateDirectory(directory);
        Write(directory, "appsettings.json", """{ "Revit": { "Release": "0000" }, "Launch": { "ShutdownTimeout": 240 } }""");
        var pipe = PipeNames.RevitSideInstance(Current.ProcessId, 2026) + ".settings." + suffix;

        var service = new RevitSide();
        using var server = PipeTransport.CreateServer(pipe);
        RevitSideChannel.BindService(server.ServiceBinder, service);
        server.Start();

        var configuration = new ConfigurationBuilder()
            .AddBhsSettings(new SettingsOptions
            {
                Side = ProcessSide.WinSide,
                ProductDirectory = directory,
                ReloadOnChange = false,
                IncludeEnvironment = false,
            })
            .Add(new PeerConfigurationSource { PipeName = pipe })
            .Build();

        var arrived = await Program.WaitForAsync(() => configuration["Revit:Release"] == "2026");

        Program.Check(ref failures, "the companion layers on top of the files", arrived);
        Program.Check(ref failures, "and what only a file says survives",
            configuration["Launch:ShutdownTimeout"] == "240");

        server.Kill();

        return failures;
    }

    private static void Write(string root, string name, string text) =>
        File.WriteAllText(Path.Combine(root, name), text);

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
