using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace BHS.Revit.Common.Parameters;

/// <summary>
/// A set of shared parameters: the files that declare them, and the binding that puts them in a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per language, one GUID per parameter.</b> The owner's decision, and it works because
/// the GUID is what makes a parameter that parameter - the name is only what it is called. So a
/// family author on a Russian Revit adds <c>BHS_Cbl_РольЭлемента</c> and one on an English Revit
/// adds <c>BHS_Cbl_ElementRole</c>, and the two families carry <b>the same parameter</b>, schedule
/// together and are read by the same code.
/// </para>
/// <para>
/// <b>Which file is used is decided by Revit's language, and English is the default</b> - see
/// <see cref="ParameterLanguages.For"/>. It follows that a model shows whichever name got there
/// first: a document bound on a Russian Revit keeps the Russian names when it is later opened in
/// English, because <c>Definition.Name</c> is read-only on all four versions and a shared parameter
/// already in a model cannot be renamed by this route. That is the intended behaviour, not a
/// limitation worked around: renaming would rewrite schedules and view filters somebody built.
/// </para>
/// <para>
/// <b>Therefore nothing here, and nothing above here, may look a parameter up by name.</b>
/// <c>SharedParameterElement.Lookup(document, guid)</c> is the only correct way to find one, and it
/// is what <see cref="Missing"/> and <see cref="Install"/> use - which also makes "is it bound?" a
/// question about the document alone, answerable without opening any file of ours.
/// </para>
/// <para>
/// <b>The files are ours and we write them, which is the owner's decision and the only one that
/// works.</b> <c>OpenSharedParameterFile</c> opens the <i>current</i> file, so creating a definition
/// of our own means assigning <c>Application.SharedParametersFilename</c>. There is no third way.
/// The predecessor in <c>..\BHS</c> already does it correctly - remember the previous path, set ours,
/// restore in a <c>finally</c> - and that form is repeated here.
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
/// <b>Revit writes the files, not us.</b> The shared parameter format is tab-delimited text with a
/// header nobody remembers correctly, and a file that is subtly wrong fails by producing no
/// definitions rather than by saying so. Creating an empty file and letting Revit fill it through
/// <c>DefinitionFile</c> costs one API call and cannot be wrong about the format.
/// </para>
/// </remarks>
public abstract class SharedParameterScheme
{
    /// <summary>The group the definitions live under inside a file, in that file's language.</summary>
    protected abstract string GroupName(ParameterLanguage language);

    /// <summary>Every parameter this scheme declares.</summary>
    protected abstract IReadOnlyList<SharedParameter> Parameters { get; }

    /// <summary>What this scheme's files are called, before the language tag.</summary>
    /// <remarks>
    /// Overridable so a scheme that is not the product's - the probe's, in particular - writes its
    /// own files rather than adding its parameters to the ones we hand to family authors. Everything
    /// path-shaped hangs off this, the parked note included, so two schemes cannot restore each
    /// other's path either.
    /// </remarks>
    protected virtual string FileBaseName => "BHS.SharedParameters";

    /// <summary>Where the file we hand to family authors lives, one per language.</summary>
    /// <remarks>
    /// Under the vendor directory rather than beside the add-in, and for the reason the settings
    /// layers are: it has to survive an uninstall and a change of edition, and a family author has
    /// to be able to find it without knowing which of our add-ins is installed.
    /// </remarks>
    public string FilePath(ParameterLanguage language) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BHS",
        FileBaseName + "." + ParameterLanguages.Tag(language) + ".txt");

    /// <summary>Where the previous path is parked while ours is in place.</summary>
    private string RestorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BHS",
        FileBaseName + ".restore.txt");

    /// <summary>Whether a path is one of the files this scheme writes.</summary>
    private bool IsOurs(string? path) => ParameterLanguages.All
        .Any(language => string.Equals(path, FilePath(language), StringComparison.OrdinalIgnoreCase));

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
    public string? RestoreInterrupted(Application application)
    {
        if (application is null || !File.Exists(RestorePath))
            return null;

        try
        {
            var previous = File.ReadAllText(RestorePath).Trim();

            // Only if one of ours is still the one in place. If the user has since chosen a third
            // file, putting back the one from before our swap would undo their choice, not ours.
            if (IsOurs(application.SharedParametersFilename))
                application.SharedParametersFilename = previous;

            File.Delete(RestorePath);
            return previous;
        }
        catch (Exception)
        {
            // A path that cannot be put back is not worth failing a command over; the note stays and
            // the next attempt tries again.
            return null;
        }
    }

    /// <summary>
    /// Writes every language's file, creating or refreshing each definition this scheme declares.
    /// </summary>
    /// <remarks>
    /// All of them, not only the one this Revit reads: the author of a family that will be shared
    /// with a differently-configured office needs the other file to exist without having to install
    /// another Revit to produce it.
    /// </remarks>
    /// <returns>The paths written, in the order of <see cref="ParameterLanguages.All"/>.</returns>
    public IReadOnlyList<string> Export(Application application)
    {
        if (application is null)
            throw new ArgumentNullException(nameof(application));

        var written = new List<string>();

        foreach (var language in ParameterLanguages.All)
        {
            var path = FilePath(language);
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(path))
                File.WriteAllText(path, string.Empty);

            WithOurFile(application, language, file =>
            {
                var group = Group(file, language);

                foreach (var declared in Parameters)
                {
                    if (Find(file, declared.Id) is not null)
                        continue;

                    var text = declared.In(language);

                    var options = new ExternalDefinitionCreationOptions(text.Name, declared.Spec)
                    {
                        GUID = declared.Id,
                        Description = text.Description,
                        Visible = true,
                        UserModifiable = true,
                    };

                    group.Definitions.Create(options);
                }
            });

            written.Add(path);
        }

        return written;
    }

    /// <summary>Which of our parameters this document does not have bound as declared.</summary>
    /// <remarks>
    /// <para>
    /// Bound "as declared" means present, on the right side of the instance/type line, and covering
    /// every category. A parameter bound to three of four categories is missing from the fourth, and
    /// saying it is present would be true and useless.
    /// </para>
    /// <para>
    /// <b>Asked by GUID, so no file is opened and no language is chosen.</b> Whether a document has
    /// the parameter is a fact about the document; routing the question through one of our files
    /// would have made the answer depend on which Revit asked, and a model bound in Russian would
    /// have read as unbound on an English one.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SharedParameter> Missing(Document document, RuntimeCategories? extra = null)
    {
        if (document is null)
            return Parameters;

        var missing = new List<SharedParameter>();
        var bindings = document.ParameterBindings;

        foreach (var declared in Parameters)
        {
            if (SharedParameterElement.Lookup(document, declared.Id) is not { } element)
            {
                missing.Add(declared);
                continue;
            }

            if (bindings.get_Item(element.GetDefinition()) is not ElementBinding binding
                || (declared.Instance && binding is not InstanceBinding)
                || (!declared.Instance && binding is not TypeBinding)
                || !Covers(binding, document, declared, extra))
            {
                missing.Add(declared);
            }
        }

        return missing;
    }

    /// <summary>
    /// Binds whatever is missing, in a transaction of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own transaction because binding a parameter is a whole act: it either happens or it does
    /// not, and there is nothing a caller would sensibly want to fold it into. The caller is a
    /// command standing on the API thread in an ordinary context - not the measured-and-uncertain
    /// one inside a modal window after an await.
    /// </para>
    /// <para>
    /// <b>Insert is tried before ReInsert, and that order is measured rather than assumed.</b>
    /// ReInsert repairs a binding that covers too few categories; on a parameter the model does not
    /// have yet it returns false and binds nothing - so a scheme that only ever called ReInsert bound
    /// nothing at all on a fresh model, which is the case that always comes first.
    /// </para>
    /// <para>
    /// <b>A parameter the document already knows is re-bound through the document's own definition,
    /// never through ours.</b> Only a parameter that is not there at all is created from the file,
    /// and only then does the language decide what it will be called - for ever, in that model. Feed
    /// our definition to a document that already holds the parameter under another language's name
    /// and the best case is that Revit ignores the name; there is no case in which it is what anyone
    /// wanted.
    /// </para>
    /// </remarks>
    /// <returns>The parameters that were bound.</returns>
    public IReadOnlyList<SharedParameter> Install(
        Document document,
        Application application,
        RuntimeCategories? extra = null)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        if (application is null)
            throw new ArgumentNullException(nameof(application));

        var missing = Missing(document, extra);

        if (missing.Count == 0)
            return missing;

        var bound = new List<SharedParameter>();
        var language = ParameterLanguages.For(application.Language);

        WithOurFile(application, language, file =>
        {
            using var transaction = new Transaction(document, "BHS: bind shared parameters");
            transaction.Start();

            foreach (var declared in missing)
            {
                // The document's own definition when it has one, ours only to introduce it.
                Definition? definition = SharedParameterElement.Lookup(document, declared.Id) is { } element
                    ? element.GetDefinition()
                    : Find(file, declared.Id);

                if (definition is null)
                    continue;

                var categories = application.Create.NewCategorySet();

                foreach (var category in Wanted(declared, extra))
                {
                    if (Category.GetCategory(document, category) is { } found)
                        categories.Insert(found);
                }

                if (categories.IsEmpty)
                    continue;

                var binding = declared.Instance
                    ? (ElementBinding)application.Create.NewInstanceBinding(categories)
                    : application.Create.NewTypeBinding(categories);

                // Insert first, ReInsert only if it refuses - and the order is the whole point.
                //
                // This was ReInsert alone, on the reasoning that Insert refuses when the definition
                // is already bound and "bound to three of the four categories we want" is exactly
                // the state Missing reports. The reasoning is right about the repair case and wrong
                // about the ordinary one: measured on 2024, 2025 and 2026, ReInsert on a parameter
                // the model does not have yet returns false and binds nothing. Which meant the
                // command bound nothing at all on a fresh model - the only case that ever happens
                // first.
                //
                // It failed silently, too: nothing threw, Install returned an empty list, and the
                // dialog said "Nothing could be bound". Found by the probe's first run.
                if (document.ParameterBindings.Insert(definition, binding, declared.Group)
                    || document.ParameterBindings.ReInsert(definition, binding, declared.Group))
                {
                    bound.Add(declared);
                }
            }

            transaction.Commit();
        });

        return bound;
    }

    /// <summary>Runs an action with one of our files as the application's, and always puts the old one back.</summary>
    private void WithOurFile(Application application, ParameterLanguage language, Action<DefinitionFile> action)
    {
        var ours = FilePath(language);
        var previous = application.SharedParametersFilename ?? string.Empty;
        var swapped = !string.Equals(previous, ours, StringComparison.OrdinalIgnoreCase);

        // Only a path that is not ours is worth parking: parking one of our own would teach
        // RestoreInterrupted to "restore" the other language's file as if it were the user's.
        var park = swapped && !IsOurs(previous);

        if (park)
            Park(previous);

        try
        {
            application.SharedParametersFilename = ours;

            // Null when the file is absent or unreadable. Nothing below can work without it, and
            // pretending otherwise would report "no parameters declared" for "no file".
            if (application.OpenSharedParameterFile() is { } file)
                action(file);
        }
        finally
        {
            if (swapped)
                application.SharedParametersFilename = previous;

            if (park)
                Unpark();
        }
    }

    private void Park(string previous)
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

    private void Unpark()
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

    private DefinitionGroup Group(DefinitionFile file, ParameterLanguage language)
    {
        var name = GroupName(language);
        return file.Groups.get_Item(name) ?? file.Groups.Create(name);
    }

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

    private static bool Covers(
        ElementBinding binding,
        Document document,
        SharedParameter declared,
        RuntimeCategories? extra)
    {
        foreach (var wanted in Wanted(declared, extra))
        {
            if (Category.GetCategory(document, wanted) is not { } category)
                continue;

            if (!binding.Categories.Contains(category))
                return false;
        }

        return true;
    }

    /// <summary>Everything a parameter should cover: what it declares, plus what the caller adds.</summary>
    /// <remarks>
    /// <b>One place, because two would drift and the drift would be silent.</b> <c>Missing</c> and
    /// <c>Install</c> have to agree about which categories count: if the check ignored a runtime
    /// category the binding covers, it would report a bound parameter as missing for ever; if the
    /// binding ignored one the check wants, every run would report it missing and bind nothing new.
    /// </remarks>
    private static IEnumerable<BuiltInCategory> Wanted(SharedParameter declared, RuntimeCategories? extra)
    {
        foreach (var category in declared.Categories)
            yield return category;

        if (extra is null)
            yield break;

        foreach (var category in extra.For(declared.Id))
        {
            if (!declared.Categories.Contains(category))
                yield return category;
        }
    }
}
