using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Settings;

namespace BHS.Revit.Host;

/// <summary>
/// What the host hands a feature. Four members, all of them already built.
/// </summary>
/// <remarks>
/// The entire composition root of the Revit side, and it is a constructor call. That is not
/// austerity for its own sake: no container may go into a Revit-side assembly - the two commonest
/// sit in Revit's own installation directory, the other two freeze their assembly version by major -
/// and with a graph this size a container would be answering a question nobody asked.
/// </remarks>
internal class FeatureServices : IFeatureServices
{
    public FeatureServices(
        IRevitContext revit,
        ISettings settings,
        IModelSettingsSource modelSettings,
        string name)
    {
        Revit = revit;
        Settings = settings;
        ModelSettings = modelSettings;
        Log = Logging.Log.For(name);
    }

    public IRevitContext Revit { get; }

    public ISettings Settings { get; }

    public IModelSettingsSource ModelSettings { get; }

    public ILog Log { get; }

    /// <summary>The same services, narrowed to one module's settings section and log category.</summary>
    /// <remarks>
    /// A module gets its own section rather than the whole store so that two modules cannot argue
    /// over a key, and its own log category so that a shared file says who wrote each line.
    /// <para>
    /// Virtual so that narrowing does not quietly demote a UI host to a DB one. A module that asked
    /// for its own section and got back services without a pump would be told it is in a
    /// <c>DBApplication</c> add-in, which would be a lie told by this method.
    /// </para>
    /// </remarks>
    public virtual IFeatureServices For(string moduleId) =>
        new FeatureServices(Revit, Settings.Section(moduleId), ModelSettings, moduleId);
}

/// <summary>The same, from a host that has a user interface: one member more.</summary>
internal sealed class UiFeatureServices : FeatureServices, IUiFeatureServices
{
    public UiFeatureServices(
        IRevitContext revit,
        IRevitApiPump pump,
        ISettings settings,
        IModelSettingsSource modelSettings,
        string name)
        : base(revit, settings, modelSettings, name)
        => Pump = pump;

    public IRevitApiPump Pump { get; }

    public override IFeatureServices For(string moduleId) =>
        new UiFeatureServices(Revit, Pump, Settings.Section(moduleId), ModelSettings, moduleId);
}
