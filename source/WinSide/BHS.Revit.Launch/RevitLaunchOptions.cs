namespace BHS.Revit.Launch;

/// <summary>How to start one Revit.</summary>
public sealed class RevitLaunchOptions
{
    /// <summary>
    /// How long to wait for the started Revit to register.
    /// </summary>
    /// <remarks>
    /// Measured cold starts across the four releases run from 53 to 72 seconds to a usable main
    /// window, so the default is roughly three times the worst of them. The wait is not really
    /// governed by this number: the process handle is watched, so a Revit that dies is noticed at
    /// once rather than at the deadline. The deadline is only for a Revit that lives and never
    /// speaks - which in practice means a modal dialog nobody is there to answer.
    /// </remarks>
    public TimeSpan RegistrationTimeout { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// How long to wait for Revit to close after being asked.
    /// </summary>
    /// <remarks>
    /// Generous because two waits are stacked inside it. Registration arrives long before Revit is
    /// idle - 18s against about 72s to a usable window on 2024 - so the external event carrying the
    /// exit command sits in the queue until startup finishes; measured at 25 seconds on 2024. Only
    /// then does teardown begin, and that alone ranged from 6s to 70s across the releases. A
    /// tighter budget does not fail the close, it kills Revit in the middle of one.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Pass <c>/nosplash</c>.
    /// </summary>
    /// <remarks>
    /// Accepted everywhere and honoured on 2026 and later only; on 2024 and 2025 the splash window
    /// appears regardless. Harmless either way - nothing here waits on a window, which is the point
    /// of waiting for a registration instead.
    /// </remarks>
    public bool NoSplash { get; set; } = true;

    /// <summary>A model to open on start, or null to start with none.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Value for <c>/language</c>, or null to leave Revit to its own setting.</summary>
    public string? Language { get; set; }

    /// <summary>Anything else to put on the command line.</summary>
    /// <remarks>
    /// The documented switches are few. <c>/nosplash</c>, <c>/runhidden</c>, <c>/runmaximized</c>,
    /// <c>/language</c>, <c>/noninteractive</c> and <c>/viewer</c> are present as strings in
    /// <c>DesktopMFC.dll</c> on 2024 through 2027, with no Autodesk documentation behind them.
    /// <para>
    /// Nothing secret goes here: another user on this machine can read a command line through WMI.
    /// That is why the correlation token travels in the environment instead.
    /// </para>
    /// </remarks>
    public IList<string> Arguments { get; } = new List<string>();

    /// <summary>Extra environment variables for the started process.</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
