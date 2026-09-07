namespace BHS.Revit.Abstractions;

/// <summary>What Revit is doing, coarsely enough to be the same on every release.</summary>
public enum RevitPhase
{
    Unspecified = 0,

    /// <summary><c>OnStartup</c> has run; the application has not finished initialising.</summary>
    Starting = 1,

    /// <summary>Nothing in progress. An external event would be processed now.</summary>
    Idle = 2,

    OpeningDocument = 3,

    /// <summary>A progress operation is running: <see cref="RevitDiagnostic.Caption"/> says which.</summary>
    Working = 4,

    DocumentReady = 5,

    /// <summary>
    /// A modal dialog is up and nothing else will happen until somebody answers it.
    /// </summary>
    /// <remarks>
    /// The state this whole mechanism is worth building for. From outside the process it is
    /// indistinguishable from a slow Revit, and this repository has diagnosed it wrongly twice -
    /// once as a lost request and once as a budget that needed raising.
    /// </remarks>
    Blocked = 6,

    Closing = 7,
}

/// <summary>
/// One thing that happened inside Revit, in terms that mean nothing about the transport.
/// </summary>
/// <remarks>
/// A plain type rather than the generated protobuf one, because this assembly is Revit-side and
/// the host deliberately does not reference the transport: an edition that never opens a channel
/// must not carry Grpc and Google.Protobuf into Revit's AppDomain to get a phase machine. Whoever
/// owns a channel translates.
/// </remarks>
public sealed class RevitDiagnostic
{
    public RevitDiagnostic(RevitPhase phase, string caption = "", string detail = "")
    {
        Phase = phase;
        Caption = caption ?? string.Empty;
        Detail = detail ?? string.Empty;
        AtUtc = DateTime.UtcNow;
    }

    public RevitPhase Phase { get; }

    public string Caption { get; }

    public string Detail { get; }

    public DateTime AtUtc { get; }

    /// <summary>Progress as Revit reports it. All zero when the phase carries none.</summary>
    public int Position { get; set; }

    public int Lower { get; set; }

    public int Upper { get; set; }

    /// <summary><c>DialogBoxShowingEventArgs.DialogId</c>, set only for <see cref="RevitPhase.Blocked"/>.</summary>
    public string DialogId { get; set; } = string.Empty;

    /// <summary>Whether this was raised on Revit's API thread.</summary>
    /// <remarks>
    /// Recorded rather than assumed. The most frequent mistake in this repository is being wrong
    /// about which thread something arrives on, and a diagnostic stream that got it wrong would be
    /// teaching that mistake rather than catching it.
    /// </remarks>
    public bool ApiThread { get; set; }

    public override string ToString() =>
        Caption.Length > 0 ? $"{Phase} - {Caption}" : Phase.ToString();
}
