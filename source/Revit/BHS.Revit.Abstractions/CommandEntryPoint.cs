using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Abstractions;

/// <summary>What a feature writes instead of an <c>IExternalCommand</c>.</summary>
/// <remarks>
/// The difference is that this one can be given things. Revit constructs an
/// <c>IExternalCommand</c> from a class name and cannot pass it anything; this is constructed by
/// the entry point, which has the host.
/// <para>
/// It is handed <see cref="IUiFeatureServices"/> rather than the narrower surface, and that is
/// exact rather than generous: a command is reached by pressing something, so the host that owns it
/// has a user interface by definition. Anything reachable from a <c>DBApplication</c> add-in is a
/// module, not a command.
/// </para>
/// </remarks>
public interface IFeatureCommand
{
    Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message);
}

/// <summary>
/// The bridge between the class name Revit resolves and the command a feature wrote.
/// </summary>
/// <remarks>
/// <para>
/// Generic so that the logic is written once, and derived from - never named directly - so that
/// Revit sees a plain type name. That combination is not a preference; both halves were measured.
/// </para>
/// <para>
/// A constructed generic name handed to Revit as text is accepted on 2026 and <b>refused on 2024</b>,
/// with a modal dialog saying the class is not found in the add-in assembly - both the short
/// spelling and the fully qualified one. The predecessor's version of this worked for six years
/// only because it wrote <c>typeof(EntryPoint&lt;Foo&gt;)</c> first, which materialises the type,
/// leaving Revit to find it rather than construct it - and which loads the feature assembly at
/// ribbon-build time, the very thing to avoid.
/// </para>
/// <para>
/// A one-line derived class solves both. The name is simple, so every release resolves it; the class
/// sits in the edition assembly, which is where Revit insists an availability class lives - measured,
/// and the failure there is a dialog too; and the CLR loads a type only when it is first needed, so
/// the base type, and with it the feature assembly, is resolved when Revit constructs this - at the
/// press, not at startup. The predecessor arrived at the same shape for its application entry point;
/// this is the same trick applied to commands.
/// </para>
/// <code>
/// [Transaction(TransactionMode.Manual)]
/// public sealed class FooCommandEntryPoint : CommandEntryPoint&lt;FooCommand&gt; { }
/// </code>
/// <para>
/// <b>The attribute goes on the derived class.</b> Revit reads <c>[Transaction]</c> off the type it
/// constructs, which is the derived one - measured, and the failure is a modal dialog saying the
/// add-in has no Transaction attribute. Putting it on the feature's own command would do nothing,
/// because Revit never sees that type.
/// <para>
/// <b>Whether it could be inherited from this base is not known, and the honest answer is worth more
/// than a tidy one.</b> Checked against the metadata of all four releases:
/// <c>TransactionAttribute</c> is declared <c>[AttributeUsage(AttributeTargets.Class)]</c> with no
/// named arguments at all, so <c>Inherited</c> keeps its default of <c>true</c> - the attribute is
/// inheritable in principle. Whether Revit asks for it with <c>inherit: true</c> has never been
/// measured; the one measurement here had no attribute anywhere in the chain, which settles nothing
/// about inheritance.
/// </para>
/// <para>
/// The rule stands anyway, and on its own merit rather than on that question: the transaction mode
/// is a property of each command, so it is stated where each command is declared. It travels in the
/// manifest and has no default - a command whose mode nobody stated is a button that fails when it
/// is pressed.
/// </para>
/// <para>
/// <b>Kept beside <see cref="CommandEntryPoint{TFeature, TCommand}"/>, which is what a feature's
/// Entry assembly uses.</b> This one finds its host by the assembly the entry point sits in, which
/// works while an edition declares its own buttons; the probe's Ping stays on it as the control.
/// </para>
/// </remarks>
public abstract class CommandEntryPoint<TCommand> : IExternalCommand
    where TCommand : IFeatureCommand, new()
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var services = Locate(commandData);

        if (services is null)
        {
            // Not a crash and not a dialog: a command whose host is missing is a deployment
            // problem, and the person in front of Revit can neither diagnose nor fix it.
            Log.For(EntryPoints.LogCategory).Error(
                "no host is registered for {0}; the add-in did not finish starting",
                typeof(TCommand).FullName);

            message = "This command is not available: its add-in did not finish starting. See the log.";
            return Result.Failed;
        }

        return EntryPoints.Run<TCommand>(services, commandData, elements, ref message);
    }

    /// <summary>
    /// Finds the host this command belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By add-in id first, because that is what Revit itself says is executing, and by the assembly
    /// this type came from second. <b>The first is measured as a raw value since 2026-09-14</b>: inside
    /// the probe's Ping command, on all four releases, <c>ActiveAddInId</c> named the probe - the add-in
    /// whose button was pressed - with the edition installed in the same process. The record before
    /// that run claimed the same and did not show it: it wrote down the host the command was handed,
    /// which the assembly fallback could have produced as well.
    /// </para>
    /// <para>
    /// The second also covers what an id cannot tell apart. One assembly can own two hosts - the probe
    /// carries an <c>Application</c> and a <c>DBApplication</c> - and an id that answers nothing, or
    /// answers a host without a user interface, still has the assembly to fall back on.
    /// </para>
    /// </remarks>
    private IUiFeatureServices? Locate(ExternalCommandData commandData)
    {
        try
        {
            var addInId = commandData?.Application?.ActiveAddInId?.GetGUID();

            if (addInId is { } id && HostRegistry.Find(id) is IUiFeatureServices found)
                return found;
        }
        catch (Exception)
        {
            // Asking must never be worse than not knowing.
        }

        // Narrowed rather than cast: a host found here that has no user interface is a
        // DBApplication add-in whose assembly also carries a command, which is a deployment
        // mistake. It reads as "no host" and gets the message below, which says what to do.
        // FindByAssembly prefers a host with a user interface, so an assembly owning both forms -
        // the probe does - does not hand back its DB half by accident of dictionary order.
        return HostRegistry.FindByAssembly(typeof(TCommand).Assembly) as IUiFeatureServices
               ?? HostRegistry.FindByAssembly(GetType().Assembly) as IUiFeatureServices;
    }
}

/// <summary>
/// The bridge for a command whose entry point lives in a feature's Entry assembly rather than in an
/// edition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the feature, not by the assembly.</b> An Entry assembly belongs to a feature and is
/// referenced by every edition that offers it, so the assembly the entry point sits in says nothing
/// about which edition is running it. What does is the edition's own <c>Modules</c> list: an edition
/// offers a feature by declaring its module, the host records the module types it declared when it
/// registers, and this asks the registry for the host with a user interface that declared
/// <typeparamref name="TFeature"/>.
/// </para>
/// <code>
/// [Transaction(TransactionMode.Manual)]
/// public sealed class RouteCablingEntryPoint : CommandEntryPoint&lt;CablingFeature, RouteCablingCommand&gt; { }
/// </code>
/// <para>
/// <b>One host is the production case</b> - one edition installed, which is the owner's rule - and
/// nothing else is consulted. <b>None</b> means the edition does not declare the feature, and the
/// command says so rather than running against services nobody composed for it. <b>More than one</b>
/// is a development state, two editions or the probe beside an edition, and only then is
/// <c>ActiveAddInId</c> read to choose. Measured raw inside a command built from an Entry manifest -
/// the probe's Gate, on all four releases, 2026-09-14 - it named the add-in whose host built the
/// button. The case it is read for, two hosts declaring one feature, has not been run; so it still
/// decides only what the feature alone cannot.
/// </para>
/// <para>
/// Naming <typeparamref name="TFeature"/> here loads nothing a press would not: a feature's module
/// type lives in its declaration assembly, which the edition loaded at startup to list it.
/// </para>
/// <para>
/// <c>[Transaction]</c> goes on the derived class, for the reasons on
/// <see cref="CommandEntryPoint{TCommand}"/>.
/// </para>
/// </remarks>
public abstract class CommandEntryPoint<TFeature, TCommand> : IExternalCommand
    where TFeature : IFeatureModule
    where TCommand : IFeatureCommand, new()
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var services = Locate(commandData, out var refusal);

        if (services is null)
        {
            message = refusal;
            return Result.Failed;
        }

        return EntryPoints.Run<TCommand>(services, commandData, elements, ref message);
    }

    /// <summary>The one host that declared the feature, or null with the sentence that says why not.</summary>
    private static IUiFeatureServices? Locate(ExternalCommandData commandData, out string refusal)
    {
        var feature = typeof(TFeature);
        var hosts = HostRegistry.FindByFeature(feature);

        if (hosts.Count == 1)
        {
            refusal = string.Empty;
            return hosts.Values.First();
        }

        var log = Log.For(EntryPoints.LogCategory);

        if (hosts.Count == 0)
        {
            // Nothing registered at all says something certain: the add-in whose button was pressed
            // never got as far as the registry. Anything registered makes "not declared" the likely
            // answer - and in a development state with several add-ins, a host that did not finish
            // starting reads the same, which the log line says rather than hides.
            if (HostRegistry.Count == 0)
            {
                log.Error("no host is registered, so {0} cannot run {1}; the add-in did not finish starting",
                    feature.FullName, typeof(TCommand).FullName);

                refusal = "This command is not available: its add-in did not finish starting. See the log.";
                return null;
            }

            log.Error(
                "no host with a user interface declares {0} in its Modules list, so {1} has nowhere to run; {2} host(s) are registered, and one that did not finish starting would read the same",
                feature.FullName, typeof(TCommand).FullName, HostRegistry.Count);

            refusal = "This command is not available: this edition does not declare the feature " +
                      feature.FullName + " in its Modules list. See the log.";
            return null;
        }

        // Several: the one Revit says is executing, and only now.
        var ids = string.Join(", ", hosts.Keys.OrderBy(id => id).Select(id => id.ToString()));
        Guid? active = null;

        try
        {
            active = commandData?.Application?.ActiveAddInId?.GetGUID();
        }
        catch (Exception)
        {
            // Asking must never be worse than not knowing; not knowing is reported below.
        }

        if (active is { } executing && hosts.TryGetValue(executing, out var chosen))
        {
            refusal = string.Empty;
            return chosen;
        }

        log.Error(
            "{0} is declared by more than one host with a user interface ({1}), and the add-in Revit names as executing, {2}, is none of them",
            feature.FullName, ids, active?.ToString() ?? "(unknown)");

        refusal = "This command is not available: more than one add-in declares its feature (" + ids +
                  "), and Revit did not name one of them as running this command. See the log.";
        return null;
    }
}

/// <summary>What both command entry points and the availability entry point share.</summary>
internal static class EntryPoints
{
    /// <summary>The category a missing host or a failing rule is reported under.</summary>
    public const string LogCategory = "BHS.Revit.Host";

    /// <summary>
    /// Runs a feature command once its host has been found. Written once, for both entry points.
    /// </summary>
    public static Result Run<TCommand>(
        IUiFeatureServices services,
        ExternalCommandData commandData,
        ElementSet elements,
        ref string message)
        where TCommand : IFeatureCommand, new()
    {
        try
        {
            return new TCommand().Execute(services, commandData, elements, ref message);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // The user pressed Escape. Not a failure, and not worth a line in the log.
            return Result.Cancelled;
        }
        catch (Exception error)
        {
            services.Log.Error(error, "command {0} failed", typeof(TCommand).FullName);
            message = error.Message;
            return Result.Failed;
        }
    }
}
