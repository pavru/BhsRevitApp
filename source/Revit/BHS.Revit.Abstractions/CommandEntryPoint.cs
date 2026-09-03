using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Abstractions;

/// <summary>What a feature writes instead of an <c>IExternalCommand</c>.</summary>
/// <remarks>
/// The difference is that this one can be given things. Revit constructs an
/// <c>IExternalCommand</c> from a class name and cannot pass it anything; this is constructed by
/// the entry point, which has the host.
/// </remarks>
public interface IFeatureCommand
{
    Result Execute(IFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message);
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
/// public sealed class FooCommandEntryPoint : CommandEntryPoint&lt;FooCommand&gt; { }
/// </code>
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
            Log.For("BHS.Revit.Host").Error(
                "no host is registered for {0}; the add-in did not finish starting",
                typeof(TCommand).FullName);

            message = "This command is not available: its add-in did not finish starting. See the log.";
            return Result.Failed;
        }

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

    /// <summary>
    /// Finds the host this command belongs to.
    /// </summary>
    /// <remarks>
    /// By add-in id first, because that is what Revit itself says is executing, and by the assembly
    /// this type came from second. The second exists because the first is documented rather than
    /// measured from inside a command, and this repository has learned what documented-but-unmeasured
    /// is worth.
    /// </remarks>
    private IFeatureServices? Locate(ExternalCommandData commandData)
    {
        try
        {
            var addInId = commandData?.Application?.ActiveAddInId?.GetGUID();

            if (addInId is { } id && HostRegistry.Find(id) is { } found)
                return found;
        }
        catch (Exception)
        {
            // Asking must never be worse than not knowing.
        }

        return HostRegistry.FindByAssembly(typeof(TCommand).Assembly)
               ?? HostRegistry.FindByAssembly(GetType().Assembly);
    }
}
