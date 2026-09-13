namespace BimHouse.RefCheck;

/// <summary>One finding about one declaration assembly.</summary>
internal readonly record struct DeclarationFinding(string Code, string File, string Message);

/// <summary>
/// Checks that a feature's declaration assembly has not reached into its own implementation.
/// </summary>
/// <remarks>
/// <para>
/// A declaration assembly holds what the application must know about a feature before anybody uses
/// it - the identifiers of things that can only be registered while Revit starts. It is therefore
/// loaded during <c>OnStartup</c> for every user of every edition, used or not, which is the whole
/// reason it must stay empty of everything else: one reference to the feature's own assemblies and
/// the laziness the ribbon manifest was built to protect is gone, silently, for everyone.
/// </para>
/// <para>
/// <b>Why a check rather than a rule in a document.</b> The mistake is not a mistake anywhere else -
/// naming your own command's type is ordinary code, and a compiler has nothing to say about it. This
/// repository has measured that exact failure once already, when <c>typeof</c> on a ribbon button
/// loaded the feature assembly while the ribbon was being built, and it has also watched a written
/// rule lapse for months because nothing executed it: RefCheck itself was never imported. So the
/// rule is enforced against the built assembly, and it reports what it checked even when it passes -
/// a check that leaves no trace is indistinguishable from one that stopped running.
/// </para>
/// <para>
/// <b>The allowed list is deliberately short and deliberately in code.</b> Growing it is a decision
/// with a name on it, which is the opposite of a setting somebody flips in a hurry. The entries are
/// the framework contracts a module cannot avoid naming: <c>IFeatureModule</c> and
/// <c>IFeatureServices</c> live in Abstractions, and the services hand out an <c>ILog</c> and an
/// <c>ISettings</c>.
/// </para>
/// </remarks>
internal static class DeclarationCheck
{
    /// <summary>Assemblies named like this are declarations, by convention of the repository.</summary>
    public const string Suffix = ".Declaration";

    private const string ImplementationCode = "RVTDEC001";
    private const string UserInterfaceCode = "RVTDEC002";

    /// <summary>The only assemblies of ours a declaration may name.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHS.Revit.Abstractions",
        "BHS.Shared",
        "BHS.Settings",
        "BHS.Logging",
    };

    /// <summary>Checks every declaration assembly in a directory. Never throws.</summary>
    /// <param name="directory">The folder to look in; usually a project's output.</param>
    /// <param name="checkedFiles">How many declaration assemblies were found and read.</param>
    public static IReadOnlyList<DeclarationFinding> Check(string directory, out int checkedFiles)
    {
        var findings = new List<DeclarationFinding>();
        checkedFiles = 0;

        if (!Directory.Exists(directory))
            return findings;

        var pattern = "*" + Suffix + ".dll";

        foreach (var file in Directory.GetFiles(directory, pattern)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyDictionary<string, string> referenced;

            try
            {
                AssemblyIndex.ReadReferences(file, out referenced);
            }
            catch (Exception error) when (error is BadImageFormatException or IOException)
            {
                continue;
            }

            checkedFiles++;

            foreach (var name in referenced.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(name, "RevitAPIUI", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new DeclarationFinding(UserInterfaceCode, file,
                        "a declaration assembly must not reference RevitAPIUI. Everything registered " +
                        "during OnStartup that a declaration exists to name - FailureDefinition first " +
                        "among them - is declared in RevitAPI, so a declaration stays usable from a " +
                        "DBApplication edition, which gets no UIControlledApplication at all. Ribbon " +
                        "buttons are not an exception to this: they are declared in the manifest, " +
                        "where Revit reads them as strings and RVTRIB001-005 can check them."));
                    continue;
                }

                if (!name.StartsWith("BHS.", StringComparison.Ordinal) || Allowed.Contains(name))
                    continue;

                findings.Add(new DeclarationFinding(ImplementationCode, file,
                    $"a declaration assembly must not reference '{name}'. This assembly is loaded " +
                    "during OnStartup for every user of every edition that declares the feature, so " +
                    "whatever it names is loaded then too - and on Revit 2024 an assembly that loads " +
                    "holds its simple name in the AppDomain shared with every other vendor for the " +
                    "rest of the session. Move what is needed into the declaration itself, or leave " +
                    "it in the implementation and let the implementation reference the declaration; " +
                    "the arrow points that way and not back. If this reference is genuinely part of " +
                    "what an application must know before a feature is used, add it to the allowed " +
                    "list in build/RefCheck/DeclarationCheck.cs, where the decision has a name on it."));
            }
        }

        return findings;
    }
}
