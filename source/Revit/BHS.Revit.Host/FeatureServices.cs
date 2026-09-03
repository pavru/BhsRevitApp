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
internal sealed class FeatureServices : IFeatureServices
{
    public FeatureServices(IRevitContext revit, IRevitApiPump pump, ISettings settings, string name)
    {
        Revit = revit;
        Pump = pump;
        Settings = settings;
        Log = Logging.Log.For(name);
    }

    public IRevitContext Revit { get; }

    public IRevitApiPump Pump { get; }

    public ISettings Settings { get; }

    public ILog Log { get; }

    /// <summary>The same services, narrowed to one module's settings section and log category.</summary>
    /// <remarks>
    /// A module gets its own section rather than the whole store so that two modules cannot argue
    /// over a key, and its own log category so that a shared file says who wrote each line.
    /// </remarks>
    public IFeatureServices For(string moduleId) =>
        new FeatureServices(Revit, Pump, Settings.Section(moduleId), moduleId);
}
