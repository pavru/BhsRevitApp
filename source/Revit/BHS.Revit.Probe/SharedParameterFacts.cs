using System.Globalization;
using Autodesk.Revit.DB;
using BHS.Revit.Abstractions;
using BHS.Revit.Common.Parameters;

namespace BHS.Revit.Probe;

/// <summary>
/// The probe's own parameter scheme, so the mechanism can be exercised without shipping parameters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own GUIDs and its own files.</b> Using the product's scheme would write the probe's
/// findings into the very files we hand to family authors, and would put two throwaway parameters
/// into whatever model the sweep opened. <c>FileBaseName</c> exists for exactly this.
/// </para>
/// <para>
/// <b>Two languages with deliberately different names</b>, because the one claim worth testing is
/// that a parameter is the same parameter in both files - and a claim about names cannot be tested
/// with names that match.
/// </para>
/// </remarks>
internal sealed class ProbeParameters : SharedParameterScheme
{
    internal const string FirstEnglish = "BHS_Prb_FirstFact";
    internal const string FirstRussian = "BHS_Prb_ПервыйФакт";

    internal static readonly Guid First = new("b1c3d6e0-9a72-4f58-8d41-5e7c2a09b634");
    internal static readonly Guid Second = new("c2d4e7f1-0b83-4a69-9e52-6f8d3b1ac745");

    protected override string FileBaseName => "BHS.ProbeParameters";

    protected override string GroupName(ParameterLanguage language) => language switch
    {
        ParameterLanguage.Russian => "BHS Проба",
        _ => "BHS Probe",
    };

    protected override IReadOnlyList<SharedParameter> Parameters { get; } = new[]
    {
        new SharedParameter(
            First,
            SpecTypeId.String.Text,
            GroupTypeId.Data,
            instance: false,
            new[] { BuiltInCategory.OST_GenericModel },
            english: new ParameterText(FirstEnglish, "A throwaway, written by the sweep."),
            russian: new ParameterText(FirstRussian, "Черновой, пишется прогоном.")),

        new SharedParameter(
            Second,
            SpecTypeId.String.Text,
            GroupTypeId.Data,
            instance: true,
            new[] { BuiltInCategory.OST_GenericModel },
            english: new ParameterText("BHS_Prb_SecondFact", "A throwaway, written by the sweep.")),
    };
}

/// <summary>
/// What the shared parameter scheme actually does inside Revit.
/// </summary>
/// <remarks>
/// <para>
/// The first automated check this mechanism has ever had, and the questions it answers are the ones
/// only a running Revit can: does swapping <c>SharedParametersFilename</c> and putting it back leave
/// the user's choice intact, do two files with the same GUIDs and different names really describe
/// one parameter, and - the load-bearing one - <b>does a document bound from one language's file
/// read as bound when asked from the other's</b>. That last claim is what makes the two-file scheme
/// safe, and until now it was an argument rather than a measurement.
/// </para>
/// <para>
/// <b>Everything written is rolled back.</b> A binding makes the document modified, and a modified
/// document makes Revit ask about saving on the way out - a modal dialog in a sweep nobody is
/// watching. The write goes through the production path unchanged, inside a
/// <c>TransactionGroup</c> that is rolled back: <c>RollBack</c> undoes transactions already
/// committed inside the group, so what is checked is what ships.
/// </para>
/// </remarks>
internal static class SharedParameterFacts
{
    public static IReadOnlyDictionary<string, string> Measure(IRevitSession session)
    {
        var answer = new Dictionary<string, string>(StringComparer.Ordinal);
        var application = session.Application.Application;
        var scheme = new ProbeParameters();

        var chosen = ParameterLanguages.For(application.Language);
        answer["parameters:language"] = application.Language.ToString();
        answer["parameters:chose"] = chosen.ToString();

        // What the user had before we touched anything. The point of the whole swap discipline is
        // that this string is identical afterwards.
        var before = application.SharedParametersFilename ?? string.Empty;
        answer["parameters:fileBefore"] = before.Length == 0 ? "(none)" : before;

        var written = scheme.Export(application);
        answer["parameters:filesWritten"] = written.Count.ToString(CultureInfo.InvariantCulture);

        var after = application.SharedParametersFilename ?? string.Empty;
        answer["parameters:filePutBack"] =
            string.Equals(before, after, StringComparison.OrdinalIgnoreCase) ? "True" : "False";

        foreach (var language in ParameterLanguages.All)
        {
            var path = scheme.FilePath(language);
            var tag = ParameterLanguages.Tag(language);

            answer["parameters:file:" + tag] = System.IO.File.Exists(path) ? "True" : "False";

            // Read each file back through Revit rather than trusting that we wrote it: the format is
            // the part that fails silently, and a file that produces no definitions is exactly how
            // it fails.
            foreach (var pair in NamesIn(application, path, tag))
                answer[pair.Key] = pair.Value;
        }

        var document = session.Application.ActiveUIDocument?.Document;

        if (document is null)
        {
            // Said out loud. A binding check that quietly does nothing on a run without a model
            // reads as a binding check that passed.
            answer["parameters:documentSkipped"] = "True";
            return answer;
        }

        answer["parameters:documentSkipped"] = "False";
        answer["parameters:missingBefore"] = scheme.Missing(document).Count
            .ToString(CultureInfo.InvariantCulture);

        using (var group = new TransactionGroup(document, "BHS probe: shared parameters"))
        {
            group.Start();

            var bound = scheme.Install(document, application);
            answer["parameters:bound"] = bound.Count.ToString(CultureInfo.InvariantCulture);
            answer["parameters:missingAfter"] = scheme.Missing(document).Count
                .ToString(CultureInfo.InvariantCulture);

            // The claim the two-file scheme rests on: found by GUID, whatever it ended up called.
            var element = SharedParameterElement.Lookup(document, ProbeParameters.First);
            answer["parameters:foundByGuid"] = element is not null ? "True" : "False";
            answer["parameters:nameInModel"] = element?.GetDefinition()?.Name ?? "(none)";

            // And the half that would bite silently: the name the model took is the one from the
            // file this Revit reads, so a machine in the other language would see the other name -
            // which is precisely why nothing may look these up by name.
            answer["parameters:nameExpected"] = chosen == ParameterLanguage.Russian
                ? ProbeParameters.FirstRussian
                : ProbeParameters.FirstEnglish;

            group.RollBack();
        }

        // Rolled back means gone, and saying so is the difference between a check that cleans up and
        // a check that says it does.
        answer["parameters:goneAfterRollback"] =
            SharedParameterElement.Lookup(document, ProbeParameters.First) is null ? "True" : "False";

        return answer;
    }

    /// <summary>Opens one written file and reports what Revit finds in it, by GUID.</summary>
    private static IEnumerable<KeyValuePair<string, string>> NamesIn(
        Autodesk.Revit.ApplicationServices.Application application,
        string path,
        string tag)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var previous = application.SharedParametersFilename ?? string.Empty;

        try
        {
            application.SharedParametersFilename = path;

            if (application.OpenSharedParameterFile() is { } file)
            {
                foreach (DefinitionGroup group in file.Groups)
                {
                    foreach (Definition definition in group.Definitions)
                    {
                        if (definition is ExternalDefinition external && external.GUID == ProbeParameters.First)
                            found["parameters:name:" + tag] = external.Name;
                    }
                }
            }
        }
        finally
        {
            application.SharedParametersFilename = previous;
        }

        if (!found.ContainsKey("parameters:name:" + tag))
            found["parameters:name:" + tag] = "(not found)";

        return found;
    }
}
