using BHS.Logging;
using BHS.Settings;

namespace BHS.Revit.Abstractions;

/// <summary>
/// Everything the framework offers a feature module, and the only thing it may depend on.
/// </summary>
/// <remarks>
/// <b>Nothing here needs a user interface</b>, which is what makes it the surface both host forms
/// can offer. An add-in can be an <c>IExternalApplication</c> or an <c>IExternalDBApplication</c>,
/// and the second one gets no <c>UIControlledApplication</c>, no ribbon and no
/// <c>ExternalEvent</c> - measured against the metadata of all four releases, where
/// <c>IExternalDBApplication</c> is declared in <c>RevitAPI</c> and hands out a plain
/// <c>ControlledApplication</c>. Everything above still works there: settings, logging, the
/// journal sink, the registry, and a document's own settings.
/// <para>
/// The way onto the API thread does not, and it is on <see cref="IUiFeatureServices"/> instead.
/// </para>
/// </remarks>
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

    /// <summary>The module's own settings section, already narrowed to it.</summary>
    ISettings Settings { get; }

    /// <summary>A log named after the module.</summary>
    ILog Log { get; }

    /// <summary>
    /// The settings that belong to a document rather than to this machine or this person.
    /// </summary>
    /// <remarks>
    /// A member of its own rather than a second narrowing of <see cref="Settings"/>: this interface
    /// is already narrowed along one axis, the module, and a document is a different axis. Folding
    /// them together would read as <c>For("x").ForDocument(d).For("y")</c>, which is two ideas
    /// wearing one name.
    /// </remarks>
    IModelSettingsSource ModelSettings { get; }

    /// <summary>
    /// The same services, narrowed to one module: its own settings section and log category.
    /// </summary>
    /// <remarks>
    /// On the interface because the host calls it for every module it starts, and a feature may want
    /// it again for something inside itself. It returns the same form it was called on - a host with
    /// a user interface does not become one without by being narrowed.
    /// </remarks>
    IFeatureServices For(string moduleId);
}

/// <summary>
/// The same services, from a host that has a user interface.
/// </summary>
/// <remarks>
/// <para>
/// A separate interface rather than a nullable <c>Pump</c> on the one above, and the choice is
/// about which mistake is possible. A nullable member is dereferenced with <c>!</c> the first time
/// the compiler complains, and the failure that follows is a <c>NullReferenceException</c> that
/// names nothing; every UI feature pays that price so that the DB form can exist. A separate
/// interface makes the mismatch a compile error where the feature is written, and a sentence where
/// it is resolved at runtime.
/// </para>
/// <para>
/// <b>The rejected third option is worth naming, because it looks the most helpful.</b> A pump in
/// the DB host that simply runs the delegate on the calling thread would satisfy every signature
/// and be wrong: the pump exists precisely for callers that are not on the API thread - measured,
/// a call over the channel always arrives on a pool thread - so running there means calling the
/// Revit API from a pool thread. That is not a refusal; it is corruption that surfaces somewhere
/// else, later.
/// </para>
/// </remarks>
public interface IUiFeatureServices : IFeatureServices
{
    /// <summary>The way onto the API thread.</summary>
    IRevitApiPump Pump { get; }
}

/// <summary>Narrowing helpers, so that a form mismatch reads as a sentence.</summary>
public static class FeatureServicesExtensions
{
    /// <summary>
    /// The services as a user-interface host offers them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When the host has no user interface. That is a deployment fact, not a bug in the caller: the
    /// same feature assembly can be reached from an <c>Application</c> add-in and from a
    /// <c>DBApplication</c> one, and only the first has a way onto the API thread.
    /// </exception>
    public static IUiFeatureServices Ui(this IFeatureServices services)
    {
        if (services is IUiFeatureServices ui)
            return ui;

        throw new InvalidOperationException(
            "This host has no user interface, so there is no way onto Revit's API thread. " +
            "The add-in was loaded as a DBApplication; a feature that needs the API thread " +
            "has to be reached from an Application add-in instead.");
    }
}
