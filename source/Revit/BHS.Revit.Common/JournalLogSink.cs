using System.Globalization;
using Autodesk.Revit.ApplicationServices;
using BHS.Logging;
using BHS.Settings;

namespace BHS.Revit.Common;

/// <summary>
/// Copies the serious records into Revit's own journal.
/// </summary>
/// <remarks>
/// <para>
/// One reason, and it is enough: the journal puts our events on the same timeline as the user's
/// operations. When somebody says "it broke after I switched views", this is the only place where
/// both halves of that sentence are written down next to each other. A file of our own cannot do
/// it, however good the timestamps.
/// </para>
/// <para>
/// <b>Only from the API thread.</b> Anything arriving over the channel lands on a pool thread, and
/// a record written from there is dropped here without ceremony - the file already has it. Routing
/// those through an <c>ExternalEvent</c> was considered and rejected: that is the same queue that
/// held the exit command for twenty-five seconds on Revit 2024, and a record that reaches the
/// journal twenty-five seconds late lands in the wrong place in the story. That would break the one
/// property the journal is being used for. Better not written than written somewhere else.
/// </para>
/// <para>
/// <b>Warning and above by default.</b> The journal travels to Autodesk with an error report; it is
/// not our private ledger. The owner chose this threshold deliberately, and
/// <c>Log:Journal:Enabled</c> turns it off entirely.
/// </para>
/// <para>
/// The practical consequence, worth accepting knowingly: channel events never reach the journal,
/// because they never arrive on the API thread. That is right. The channel is our own machinery and
/// belongs in our own file; the journal gets what happened in the user's session.
/// </para>
/// </remarks>
public sealed class JournalLogSink : ILogSink, IConfigurableLogSink
{
    private readonly ControlledApplication _application;

    /// <param name="application">
    /// Taken as <c>ControlledApplication</c> rather than <c>Application</c> because that is what
    /// <c>OnStartup</c> hands over, and startup is where this has to be attached to be of any use.
    /// </param>
    public JournalLogSink(ControlledApplication application) =>
        _application = application ?? throw new ArgumentNullException(nameof(application));

    public LogLevel Minimum { get; private set; } = LogLevel.Warning;

    public void Configure(ISettings settings)
    {
        var section = settings.Section("Log").Section("Journal");

        if (!section.Flag("Enabled", true))
        {
            Minimum = LogLevel.None;
            return;
        }

        var declared = section["Level"];

        Minimum = string.IsNullOrEmpty(declared)
            ? LogLevel.Warning
            : ParseOrWarning(declared!);
    }

    public void Emit(in LogEntry entry)
    {
        // The record itself knows, because the router asked at the moment of writing. Comparing
        // thread ids here would be the same test one call later and no more reliable.
        if (!entry.OnPrimaryThread)
            return;

        var text = string.Format(
            CultureInfo.InvariantCulture,
            "BHS {0} {1}: {2}",
            entry.Level.ToString().ToUpperInvariant(),
            entry.Category,
            entry.Message);

        if (entry.Error is not null)
            text += "  <- " + entry.Error.GetType().Name + ": " + entry.Error.Message;

        // timeStamp: true, so the line carries Revit's own clock rather than ours. Correlating with
        // the surrounding journal entries is the entire point.
        _application.WriteJournalComment(text, true);
    }

    private static LogLevel ParseOrWarning(string value)
    {
        foreach (LogLevel candidate in Enum.GetValues(typeof(LogLevel)))
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return LogLevel.Warning;
    }

    public void Dispose()
    {
    }
}
