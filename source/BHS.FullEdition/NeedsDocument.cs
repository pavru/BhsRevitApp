using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BHS.FullEdition;

/// <summary>
/// Greys out a command that has nothing to read.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the edition's assembly because Revit gives it no choice.</b> The class name on a button is
/// resolved inside the assembly the button names, so an availability class supplied by the
/// framework would never be found - and the failure is a modal <c>TypeLoadException</c> in front of
/// the user, not a greyed-out button. Measured, which is why the framework does not try.
/// </para>
/// <para>
/// <b>Grey rather than an error, and only for the one thing a person can see.</b> A command that
/// cannot run because there is no document is obvious the moment you look at Revit; a command
/// greyed out for a reason the user cannot see is worse than one that runs and explains itself.
/// Everything else - no trays in the model, no circuits - is left to the command, which can say so.
/// </para>
/// </remarks>
public sealed class NeedsDocument : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData?.ActiveUIDocument?.Document is not null;
}
