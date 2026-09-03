using Autodesk.Revit.UI;

namespace BHS.Revit.Abstractions;

/// <summary>
/// Revit as it is only ever seen from the API thread.
/// </summary>
/// <remarks>
/// Handed to work running inside <see cref="IRevitApiPump"/>, and nowhere else. That is the whole
/// point of it being a separate type from <see cref="IRevitContext"/>: a <c>UIApplication</c> that
/// can be stored is a <c>UIApplication</c> that will be used from the wrong thread, and this one
/// cannot be stored usefully because it is only handed to code that is already on the right one.
/// </remarks>
public interface IRevitSession
{
    /// <summary>The session, valid for the duration of this call and no longer.</summary>
    UIApplication Application { get; }
}
