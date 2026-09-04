using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// The document's own settings, kept inside the document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extensible Storage rather than a file beside the model</b>, and one fact settles it: a cloud
/// model and a detached one have an empty <c>PathName</c>. There is no "beside" for them, so a
/// scheme built on it would work for some projects and silently not for others. Settings that
/// belong to a project travel with the project.
/// </para>
/// <para>
/// <b>Three fields, and never a fourth.</b> A schema that has gone into a customer's model can
/// never be changed - the schema registry is one per Revit process and there is no redefinition -
/// while keys inside a map we can change whenever we like. So the schema carries a version, a map
/// of what is set, and a list of what is cleared; everything else is expressed in keys. The
/// transport made the same trade for the same reason: the shape is typed, the key space is not.
/// </para>
/// <para>
/// <b>Cleared keys need a field of their own</b> because Extensible Storage refuses a null value.
/// Clearing matters: it is how a project removes something the product or machine file set, leaving
/// the consumer with its own default rather than the vendor's. Read back, a cleared key becomes a
/// null value - the very shape the settings merge already understands.
/// </para>
/// </remarks>
internal sealed class ModelSettingsStore
{
    /// <summary>
    /// The schema's identity, and it is permanent.
    /// </summary>
    /// <remarks>
    /// Generated once, deliberately, and recorded in <c>CLAUDE.md</c> beside the rule that every
    /// add-in needs an <c>AddInId</c> of its own. A schema that has been written into somebody's
    /// model cannot be redefined afterwards, so this value outlives every decision around it.
    /// </remarks>
    public static readonly Guid SchemaId = new("7b2b6a1e-2f4a-4a3d-9c1e-0f8a5d3c7e10");

    public const string SchemaName = "BHSModelSettings";
    public const string VersionField = "Version";
    public const string ValuesField = "Values";
    public const string ClearedField = "Cleared";

    private readonly ILog _log = Log.For<ModelSettingsStore>();

    /// <summary>
    /// Reads what one document holds. Never throws.
    /// </summary>
    /// <remarks>
    /// A document that cannot be read is reported as such and answers from the layers below it: a
    /// project rule nobody can read is a reason to say so in the log, not a reason to stop working.
    /// </remarks>
    public bool TryRead(Document document, out IDictionary<string, string?> values)
    {
        values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var schema = Schema.Lookup(SchemaId);

            if (schema is null)
                return true; // Nothing has defined it in this process yet, so nothing wrote it either.

            var storage = Find(document, schema);

            if (storage is null)
                return true;

            var entity = storage.GetEntity(schema);

            if (entity is null || !entity.IsValid())
                return true;

            foreach (var pair in entity.Get<IDictionary<string, string>>(ValuesField))
                values[pair.Key] = pair.Value;

            // Unfolded back into the shape the merge understands: present, and null.
            foreach (var key in entity.Get<IList<string>>(ClearedField))
                values[key] = null;

            return true;
        }
        catch (Exception error)
        {
            _log.Warn(error, "could not read the settings held in {0}", Title(document));
            return false;
        }
    }

    /// <summary>Writes the document's own layer. On the API thread, inside one transaction.</summary>
    public void Write(Document document, IReadOnlyDictionary<string, string?> values)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        var schema = Schema.Lookup(SchemaId) ?? Build();
        var entity = new Entity(schema);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cleared = new List<string>();

        foreach (var pair in values)
        {
            if (pair.Value is null)
                cleared.Add(pair.Key);
            else
                set[pair.Key] = pair.Value;
        }

        entity.Set(VersionField, "1");
        entity.Set<IDictionary<string, string>>(ValuesField, set);
        entity.Set<IList<string>>(ClearedField, cleared);

        using var transaction = new Transaction(document, "BHS model settings");
        transaction.Start();

        var storage = Find(document, schema) ?? DataStorage.Create(document);
        storage.SetEntity(entity);

        transaction.Commit();

        _log.Info("wrote {0} value(s) and {1} cleared key(s) into {2}",
            set.Count, cleared.Count, Title(document));
    }

    /// <summary>
    /// Defines the schema, once per Revit process.
    /// </summary>
    /// <remarks>
    /// Read access is <c>Public</c> by the owner's decision: our own tools under another vendor id,
    /// and anything reading the model outside Revit, have to be able to see a project's rules. The
    /// cost is accepted knowingly - any add-in in the session can read them. Writing stays with us.
    /// </remarks>
    private static Schema Build()
    {
        var builder = new SchemaBuilder(SchemaId);

        builder.SetSchemaName(SchemaName);
        builder.SetVendorId("BimHouseSoftware");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Vendor);
        builder.SetDocumentation("BHS framework settings that belong to this model.");

        builder.AddSimpleField(VersionField, typeof(string));
        builder.AddMapField(ValuesField, typeof(string), typeof(string));
        builder.AddArrayField(ClearedField, typeof(string));

        return builder.Finish();
    }

    private static DataStorage? Find(Document document, Schema schema)
    {
        using var collector = new FilteredElementCollector(document);

        return collector
            .OfClass(typeof(DataStorage))
            .WherePasses(new ExtensibleStorageFilter(schema.GUID))
            .Cast<DataStorage>()
            .FirstOrDefault();
    }

    private static string Title(Document document)
    {
        try
        {
            return document.Title;
        }
        catch (Exception)
        {
            return "(a document)";
        }
    }
}
