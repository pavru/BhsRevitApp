using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace BHS.Revit.Sdk;

/// <summary>
///     MSBuild Task to generate DefineConstants for Revit versions.
///     Similar to how .NET SDK generates NET_X_0, NET_X_0_OR_GREATER, etc.
///     For example, if RevitVersion is 2026, it generates:
///     REVIT; REVIT2026; REVIT2024_OR_GREATER; REVIT2025_OR_GREATER; REVIT2026_OR_GREATER
/// </summary>
[PublicAPI]
public class GenerateRevitDefineConstants : Task
{
    /// <summary>
    ///     The current Revit version parsed from the TargetFramework (e.g. "2026")
    /// </summary>
    [Required]
    public string RevitVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Existing defined constants to which the new ones will be appended.
    /// </summary>
    public string DefineConstants { get; set; } = string.Empty;

    /// <summary>
    ///     Minimum supported Revit version to start generating _OR_GREATER constants.
    /// </summary>
    public int MinSupportedVersion { get; set; } = 2024;

    /// <summary>
    ///     The resulting DefineConstants string with added Revit constants.
    /// </summary>
    [Output]
    public string UpdatedDefineConstants { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (!int.TryParse(RevitVersion, out var currentVersion))
        {
            Log.LogError($"Invalid RevitVersion format: '{RevitVersion}'. It must be an integer.");
            return false;
        }

        var constants = new HashSet<string>(
            (DefineConstants ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase
        )
        {
            // Add base constants
            "REVIT",
            $"REVIT{currentVersion}"
        };

        // Add _OR_GREATER constants
        for (var version = MinSupportedVersion; version <= currentVersion; version++)
            constants.Add($"REVIT{version}_OR_GREATER");

        UpdatedDefineConstants = string.Join(";", constants);

        return true;
    }
}