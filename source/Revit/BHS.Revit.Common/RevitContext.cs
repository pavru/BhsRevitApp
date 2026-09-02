using System.Threading;
using Autodesk.Revit.UI;
using BHS.Revit.Abstractions;
using BHS.Shared;

namespace BHS.Revit.Common;

/// <summary>
/// The context handed to features. Captures the API thread at construction, which only works
/// because the framework builds it on that thread during OnStartup.
/// </summary>
public sealed class RevitContext : IRevitContext
{
    public RevitContext(UIApplication application)
    {
        Application = application;
        ApiThreadId = Thread.CurrentThread.ManagedThreadId;
        Release = RevitRelease.Parse(application.Application.VersionNumber);
    }

    public RevitRelease Release { get; }

    public int ApiThreadId { get; }

    public UIApplication Application { get; }
}
