using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.MEP.Cabling.Revit;
using BHS.Revit.Abstractions;
using BHS.Revit.Common.Parameters;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Writes the shared parameter files and binds what this model is missing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two jobs in one command because they are one act, and the first of them has a user waiting.</b>
/// A family author needs the file before any of our code runs: our parameters go into a junction box
/// family from <i>this</i> file, and a parameter added from any other file has a different GUID and
/// is a different parameter that happens to share a name. So the command writes the files to a fixed
/// place under the vendor directory and says where, whether or not anything needed binding.
/// </para>
/// <para>
/// <b>Both languages are written, and the dialog says which one this Revit reads.</b> Writing only
/// the local one would leave an author who has to hand a family to a differently-configured office
/// with no way to produce the other file short of installing another Revit - and the two files
/// declare the same GUIDs, so there is nothing to keep in step by hand.
/// </para>
/// <para>
/// <b>It is a write, and an ordinary one.</b> Binding a parameter is a modification, so this is a
/// transaction - but it is opened from a command standing on the API thread in the context Revit
/// hands it, not the measured one inside a modal window after an await.
/// </para>
/// </remarks>
public sealed class InstallParametersCommand : IFeatureCommand
{
    public Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var document = data?.Application?.ActiveUIDocument?.Document;
        var application = data?.Application?.Application;

        if (document is null || application is null)
        {
            message = "Open a model: this command needs a document.";
            return Result.Failed;
        }

        var scheme = new CablingParameters();
        var log = services.Log;
        var language = ParameterLanguages.For(application.Language);

        // Before anything else: if a previous swap was interrupted by a process that died, the user
        // is still pointing at our file instead of theirs. Here rather than at startup, and that is
        // a limitation worth naming - SharedParametersFilename lives on Application, which OnStartup
        // does not have; it has ControlledApplication. So the repair happens the next time somebody
        // touches shared parameters, which is the soonest this code can reach the property at all.
        if (scheme.RestoreInterrupted(application) is { } restored)
            log.Warn("parameters: an interrupted swap was undone, the file is back to {0}", restored);

        var files = scheme.Export(application);
        var active = scheme.FilePath(language);

        foreach (var file in files)
            log.Info("parameters: wrote {0}", file);

        log.Info("parameters: this Revit speaks {0}, so it reads {1}", application.Language, active);

        var missing = scheme.Missing(document);

        if (missing.Count == 0)
        {
            log.Info("parameters: this model already has all of them bound");
            Show(active, files, language, bound: Array.Empty<SharedParameter>(), alreadyThere: true);
            return Result.Succeeded;
        }

        var bound = scheme.Install(document, application);

        foreach (var one in bound)
            log.Info("parameters: bound {0} to {1} category(ies)", one.In(language).Name, one.Categories.Count);

        // Named rather than counted. "Two of three bound" leaves the reader to work out which one
        // did not, and the one that did not is the whole message.
        foreach (var one in missing)
        {
            if (!bound.Contains(one))
                log.Warn("parameters: {0} could not be bound", one.In(language).Name);
        }

        Show(active, files, language, bound, alreadyThere: false);
        return Result.Succeeded;
    }

    /// <remarks>
    /// A <c>TaskDialog</c> for the same reason the collect command uses one: a window of our own
    /// belongs to the design pass, and borrowing Revit's costs nothing and cannot quietly become the
    /// design.
    /// </remarks>
    private static void Show(
        string active,
        IReadOnlyList<string> files,
        ParameterLanguage language,
        IReadOnlyList<SharedParameter> bound,
        bool alreadyThere)
    {
        var others = files.Where(file => !string.Equals(file, active, StringComparison.OrdinalIgnoreCase));

        var body = string.Format(
            CultureInfo.CurrentCulture,
            "This Revit reads:\n{0}\n\nPoint Revit at it when adding these parameters to a family - "
            + "a parameter added from another vendor's file has a different identity, whatever it is "
            + "called.\n\nAlso written, for offices running Revit in another language:\n{1}\n\n"
            + "Both files declare the same parameters under the same identifiers, so a family built "
            + "from either one is understood by both.",
            active,
            string.Join("\n", others));

        var dialog = new TaskDialog("Shared parameters")
        {
            MainInstruction = alreadyThere
                ? "This model already has them"
                : bound.Count == 0
                    ? "Nothing could be bound"
                    : string.Format(CultureInfo.CurrentCulture, "{0} parameter(s) bound", bound.Count),
            MainContent = body,
            ExpandedContent = bound.Count == 0
                ? null
                : string.Join("\n", bound.Select(one => one.In(language).Name)),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        dialog.Show();
    }
}
