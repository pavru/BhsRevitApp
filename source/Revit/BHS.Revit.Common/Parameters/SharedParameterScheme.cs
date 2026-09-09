using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace BHS.Revit.Common.Parameters;

/// <summary>
/// A set of shared parameters: the file that declares them, and the binding that puts them in a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file is ours and we write it, which is the owner's decision and the only one that works.</b>
/// <c>OpenSharedParameterFile</c> opens the <i>current</i> file, so creating a definition of our own
/// means assigning <c>Application.SharedParametersFilename</c>. There is no third way. The predecessor
/// in <c>..\BHS</c> already does it correctly - remember the previous path, set ours, restore in a
/// <c>finally</c> - and that form is repeated here.
/// </para>
/// <para>
/// <b>One improvement over it, aimed at the single failure a <c>finally</c> cannot cover.</b> A
/// <c>finally</c> does not run when the process dies: Revit killed, crashed, taken down by a sweep
/// past its deadline. The user is then left pointing at our file instead of theirs, for ever. So the
/// previous path is written to a file beside the log <b>before</b> the swap and removed after it, and
/// <see cref="RestoreInterrupted"/> puts back whatever an interrupted swap left behind. The window of
/// damage goes from "for ever" to "until we are next asked about shared parameters" - see the remarks
/// on that method for why it is not "until the next launch", which is where it belongs and where the
/// API does not reach.
/// </para>
/// <para>
/// <b>Revit writes the file, not us.</b> The shared parameter format is tab-delimited text with a
/// header nobody remembers correctly, and a file that is subtly wrong fails by producing no
/// definitions rather than by saying so. Creating an empty file and letting Revit fill it through
/// <c>DefinitionFile</c> costs one API call and cannot be wrong about the format.
/// </para>
/// </remarks>
public abstract class SharedParameterScheme
{
    /// <summary>The group the definitions live under inside the file.</summary>
    protected abstract string GroupName { get; }

    /// <summary>Every parameter this scheme declares.</summary>
    protected abstract IReadOnlyList<SharedParameter> Parameters { get; }

    /// <summary>Where the file we hand to family authors lives.</summary>
    /// <remarks>
    /// Under the vendor directory rather than beside the add-in, and for the reason the settings
    /// layers are: it has to survive an uninstall and a change of edition, and a family author has
    /// to be able to find it without knowing which of our add-ins is installed.
    /// </remarks>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BHS",
        "BHS.SharedParameters.txt");

    /// <summary>Where the previous path is parked while ours is in place.</summary>
    private static string RestorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BHS",
        "shared-parameters-restore.txt");

    /// <summary>
    /// Puts back a shared parameter file path that an interrupted swap left ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silent when there is nothing to do, which is almost always: the note only exists between the
    /// two halves of a swap, and a swap that finished removed it.
    /// </para>
    /// <para>
    /// <b>Called from a command rather than from startup, and the reason is a limitation.</b>
    /// <c>SharedParametersFilename</c> is on <c>Application</c>; <c>OnStartup</c> has
    /// <c>ControlledApplication</c>, which is not the same object and does not carry it. So the
    /// earliest this code can reach the property is the next time somebody asks us to touch shared
    /// parameters - later than ideal, and still the first moment available.
    /// </para>
    /// </remarks>
    public static string? RestoreInterrupted(Application application)
    {
        if (application is null || !File.Exists(RestorePath))
            return null;

        try
        {
            var previous = File.ReadAllText(RestorePath).Trim();

            // Only if ours is still the one in place. If the user has since chosen a third file,
            // putting back the one from before our swap would undo their choice, not ours.
            if (string.Equals(application.SharedParametersFilename, FilePath, StringComparison.OrdinalIgnoreCase))
                application.SharedParametersFilename = previous;

            File.Delete(RestorePath);
            return previous;
        }
        catch (Exception)
        {
            // A path that cannot be put back is not worth failing a startup over; the note stays and
            // the next launch tries again.
            return null;
        }
    }

    /// <summary>Writes the file, creating or refreshing every definition this scheme declares.</summary>
    /// <returns>The path written.</returns>
    public string Export(Application application)
    {
        if (application is null)
            throw new ArgumentNullException(nameof(application));

        var directory = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, string.Empty);

        WithOurFile(application, file =>
        {
            var group = Group(file);

            foreach (var declared in Parameters)
            {
                if (Find(file, declared.Id) is not null)
                    continue;

                var options = new ExternalDefinitionCreationOptions(declared.Name, declared.Spec)
                {
                    GUID = declared.Id,
                    Description = declared.Description,
                    Visible = true,
                    UserModifiable = true,
                };

                group.Definitions.Create(options);
            }
        });

        return FilePath;
    }

    /// <summary>Which of our parameters this document does not have bound as declared.</summary>
    /// <remarks>
    /// Bound "as declared" means present, on the right side of the instance/type line, and covering
    /// every category. A parameter bound to three of four categories is missing from the fourth, and
    /// saying it is present would be true and useless.
    /// </remarks>
    public IReadOnlyList<SharedParameter> Missing(Document document, Application application)
    {
        if (document is null || application is null)
            return Parameters;

        var missing = new List<SharedParameter>();
        var bindings = document.ParameterBindings;

        WithOurFile(application, file =>
        {
            foreach (var declared in Parameters)
            {
                if (Find(file, declared.Id) is not { } definition)
                {
                    missing.Add(declared);
                    continue;
                }

                if (bindings.get_Item(definition) is not ElementBinding binding
                    || (declared.Instance && binding is not InstanceBinding)
                    || (!declared.Instance && binding is not TypeBinding)
                    || !Covers(binding, document, declared))
                {
                    missing.Add(declared);
                }
            }
        });

        return missing;
    }

    /// <summary>
    /// Binds whatever is missing, in a transaction of its own.
    /// </summary>
    /// <remarks>
    /// Its own transaction because binding a parameter is a whole act: it either happens or it does
    /// not, and there is nothing a caller would sensibly want to fold it into. The caller is a
    /// command standing on the API thread in an ordinary context - not the measured-and-uncertain
    /// one inside a modal window after an await.
    /// </remarks>
    /// <returns>The parameters that were bound.</returns>
    public IReadOnlyList<SharedParameter> Install(Document document, Application application)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        var missing = Missing(document, application);

        if (missing.Count == 0)
            return missing;

        var bound = new List<SharedParameter>();

        WithOurFile(application, file =>
        {
            using var transaction = new Transaction(document, "BHS: bind shared parameters");
            transaction.Start();

            foreach (var declared in missing)
            {
                if (Find(file, declared.Id) is not { } definition)
                    continue;

                var categories = application.Create.NewCategorySet();

                foreach (var category in declared.Categories)
                {
                    if (Category.GetCategory(document, category) is { } found)
                        categories.Insert(found);
                }

                if (categories.IsEmpty)
                    continue;

                var binding = declared.Instance
                    ? (ElementBinding)application.Create.NewInstanceBinding(categories)
                    : application.Create.NewTypeBinding(categories);

                // ReInsert rather than Insert: Insert refuses when the definition is already bound,
                // and "bound to three of the four categories we want" is exactly the state Missing
                // reports and this has to repair.
                if (document.ParameterBindings.ReInsert(definition, binding, declared.Group))
                    bound.Add(declared);
            }

            transaction.Commit();
        });

        return bound;
    }

    /// <summary>Runs an action with our file as the application's, and always puts the old one back.</summary>
    private static void WithOurFile(Application application, Action<DefinitionFile> action)
    {
        var previous = application.SharedParametersFilename ?? string.Empty;
        var swapped = !string.Equals(previous, FilePath, StringComparison.OrdinalIgnoreCase);

        if (swapped)
            Park(previous);

        try
        {
            application.SharedParametersFilename = FilePath;

            // Null when the file is absent or unreadable. Nothing below can work without it, and
            // pretending otherwise would report "no parameters declared" for "no file".
            if (application.OpenSharedParameterFile() is { } file)
                action(file);
        }
        finally
        {
            if (swapped)
            {
                application.SharedParametersFilename = previous;
                Unpark();
            }
        }
    }

    private static void Park(string previous)
    {
        try
        {
            var directory = Path.GetDirectoryName(RestorePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(RestorePath, previous);
        }
        catch (Exception)
        {
            // The note is a safety net for a process that dies mid-swap. Failing to write it is not
            // a reason to refuse the work it was protecting.
        }
    }

    private static void Unpark()
    {
        try
        {
            if (File.Exists(RestorePath))
                File.Delete(RestorePath);
        }
        catch (Exception)
        {
        }
    }

    private DefinitionGroup Group(DefinitionFile file) =>
        file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);

    private static ExternalDefinition? Find(DefinitionFile file, Guid id)
    {
        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition definition in group.Definitions)
            {
                if (definition is ExternalDefinition external && external.GUID == id)
                    return external;
            }
        }

        return null;
    }

    private static bool Covers(ElementBinding binding, Document document, SharedParameter declared)
    {
        foreach (var wanted in declared.Categories)
        {
            if (Category.GetCategory(document, wanted) is not { } category)
                continue;

            if (!binding.Categories.Contains(category))
                return false;
        }

        return true;
    }
}
