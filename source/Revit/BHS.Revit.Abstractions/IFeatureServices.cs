using BHS.Logging;
using BHS.Settings;

namespace BHS.Revit.Abstractions;

/// <summary>
/// Everything the framework offers a feature module, and the only thing it may depend on.
/// </summary>
/// <remarks>
/// Deliberately a narrow, hand-written surface rather than a container. There is no dependency
/// injection container in Revit-side code and there will not be one: measured, Castle.Windsor and
/// Unity sit in Revit's own installation directory, so our copies lose by definition, while Autofac
/// and SimpleInjector freeze their assembly version by major, which is the Newtonsoft failure in a
/// different coat. Composition is done with constructors, and this interface is what a feature is
/// composed from.
/// <para>
/// Should that ever stop being enough, a container goes <em>behind</em> this interface, inside the
/// host, and no feature module changes - they cannot see how their graph was built and never could.
/// That reversibility is the reason to start here.
/// </para>
/// </remarks>
public interface IFeatureServices
{
    /// <summary>The running Revit, minus everything that is only valid on the API thread.</summary>
    IRevitContext Revit { get; }

    /// <summary>The way onto the API thread.</summary>
    IRevitApiPump Pump { get; }

    /// <summary>The module's own settings section, already narrowed to it.</summary>
    ISettings Settings { get; }

    /// <summary>A log named after the module.</summary>
    ILog Log { get; }
}
