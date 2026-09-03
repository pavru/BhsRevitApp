namespace BHS.Revit.Abstractions;

/// <summary>
/// A feature module, for the ones that need code to run rather than only buttons to exist.
/// </summary>
/// <remarks>
/// <para>
/// Smaller than it first was, and the shrinking is the point. An earlier version had the module
/// describe its ribbon in code, which meant loading every feature assembly during
/// <c>OnStartup</c> to ask. The ribbon is built from manifests beside the assemblies instead, so a
/// module that only contributes buttons implements nothing at all and is never loaded until one is
/// pressed.
/// </para>
/// <para>
/// The cost of getting that wrong would not have been milliseconds of startup. On Revit 2024 every
/// assembly that loads holds its simple name in the AppDomain shared with every other vendor for the
/// rest of the session - including features nobody touched.
/// </para>
/// </remarks>
public interface IFeatureModule
{
    /// <summary>
    /// Brings the module up. On the API thread.
    /// </summary>
    /// <remarks>
    /// Called during <c>OnStartup</c> for a module marked eager, and otherwise the first time
    /// something in it is used. A module that throws here is logged and skipped: one broken feature
    /// must not cost the others, and inside Revit an exception escaping startup is a dialog on a
    /// machine with nobody in front of it.
    /// </remarks>
    void Start(IFeatureServices services);

    /// <summary>Takes it down again, on the API thread, during <c>OnShutdown</c>.</summary>
    void Stop();
}
