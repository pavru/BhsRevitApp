using Autodesk.Revit.UI;
using BHS.Shared;

namespace BHS.Revit.Abstractions;

/// <summary>
/// What a feature is handed to reach the running Revit session. Deliberately narrow: everything
/// version specific is resolved by the framework before a feature sees it.
/// </summary>
public interface IRevitContext
{
    /// <summary>The release this assembly was built for.</summary>
    RevitRelease Release { get; }

    /// <summary>The thread Revit calls its API on. Every API call has to happen there.</summary>
    int ApiThreadId { get; }

    UIApplication Application { get; }
}
