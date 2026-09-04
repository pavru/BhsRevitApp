using Autodesk.Revit.DB;
using BHS.Settings;

namespace BHS.Revit.Abstractions;

/// <summary>Where a document's own settings came from, and whether they can be written.</summary>
public enum ModelSettingsOrigin
{
    /// <summary>The document holds none; what is read comes from the layers below it.</summary>
    None,

    /// <summary>Read out of the document.</summary>
    Model,

    /// <summary>The document could not be read - see the log. The layers below still answer.</summary>
    Unreadable,
}

/// <summary>
/// The settings of one document, layered over the ones the process read from disk.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="ISettings"/> deliberately, and that is the main decision here: every
/// reader already written - <c>Text</c>, <c>Flag</c>, <c>Number</c>, <c>Duration</c> - works over
/// these without a line of new code, and a feature handed one of these instead of the process
/// settings does not notice the difference.
/// </para>
/// <para>
/// <b>Scope is a property of the key, not of a layer.</b> A key under <c>Model:</c> is a project
/// rule: it reads the product and machine files, then the model, then the environment - and the
/// user's own file is not in that chain at all. Everything else is a preference and reads the
/// ordinary chain, where the model has no say. The question "is the model stronger than the user"
/// has no answer because it is asked of the wrong thing.
/// </para>
/// </remarks>
public interface IModelSettings : ISettings
{
    /// <summary>Which document these belong to.</summary>
    Document Document { get; }

    /// <summary>Whether the document held anything, and whether it could be read.</summary>
    ModelSettingsOrigin Origin { get; }
}

/// <summary>
/// Where model settings come from.
/// </summary>
/// <remarks>
/// There is no "settings of the active document" here, and its absence is the design. A call
/// arriving over the channel is never on Revit's API thread and may find no active document at all;
/// an API shaped as <c>Active</c> would let both failures be written by accident. A
/// <see cref="Document"/> in this process comes from exactly two places, and both are already
/// behind the right door: <c>IRevitSession</c>, which is only handed to code running inside the
/// pump, and the parameters Revit passes to a command or an event.
/// </remarks>
public interface IModelSettingsSource
{
    /// <summary>
    /// The settings for one document. Never null: a document holding nothing yields the layers
    /// below it.
    /// </summary>
    IModelSettings For(Document document);

    /// <summary>
    /// Writes the document's own layer. One transaction, on the API thread, on a deliberate act.
    /// </summary>
    /// <remarks>
    /// A null value means "clear", which is not the same as absent: it removes what the product or
    /// machine file put there and lets the consumer see its own default. Extensible Storage cannot
    /// hold a null, so cleared keys travel in a list of their own and are unfolded back on reading -
    /// into the very shape the merge already understands.
    /// </remarks>
    void Write(Document document, IReadOnlyDictionary<string, string?> values);
}
