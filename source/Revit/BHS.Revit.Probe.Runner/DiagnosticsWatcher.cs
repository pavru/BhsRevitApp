using System.Globalization;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// Follows one Revit instance for as long as the sweep lasts, and says what it saw.
/// </summary>
/// <remarks>
/// <para>
/// <b>For the whole run, not for a moment.</b> The first version of this subscribed, listened for
/// twenty seconds and reported - and measured one event, fewer than the same sweep without a model.
/// Nothing was wrong with the stream: the document arrived a minute later, long after the listener
/// had gone. A watcher that samples cannot see phases, because a phase is a thing with a beginning
/// and an end.
/// </para>
/// <para>
/// It is also the shape the eventual watchdog needs. Waiting on a fixed budget is what this
/// repository does today, and the same wait has measured 696 seconds and 16.3; waiting on silence
/// needs somebody listening the whole time to know what silence is.
/// </para>
/// <para>
/// A connection of its own, which is tidy rather than necessary. The reason first written here -
/// that a long stream would occupy one of the library's four listeners - is wrong: the forked
/// GrpcDotNetNamedPipes hands each accepted connection to Task.Run and the listening thread
/// immediately creates a fresh pipe instance, so a stream held open costs no listener. Corrected
/// rather than deleted, because a plausible wrong reason is how a decision survives being
/// re-examined.
/// </para>
/// </remarks>
internal sealed class DiagnosticsWatcher : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly HashSet<RevitPhase> _phases = new();
    private readonly List<string> _captions = new();
    private readonly List<string> _dialogs = new();
    private readonly Task _reading;

    private int _received;
    private int _dropped;
    private int _offApiThread;
    private int _gaps;
    private long _lastSequence;
    private long _lastAtUnixMs;
    private DateTime _lastHeardUtc = DateTime.UtcNow;
    private string _blockedBy = string.Empty;
    private TimeSpan _longestSilence = TimeSpan.Zero;
    private RevitPhase _silencePhase = RevitPhase.Unspecified;
    private RevitPhase _phase = RevitPhase.Unspecified;
    private string _failure = string.Empty;

    public DiagnosticsWatcher(string pipeName)
    {
        var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(pipeName));
        _reading = Task.Run(() => ReadAsync(client, _stopping.Token));
    }

    public int Received { get { lock (_gate) return _received; } }

    public int Dropped { get { lock (_gate) return _dropped; } }

    public int OffApiThread { get { lock (_gate) return _offApiThread; } }

    public int Gaps { get { lock (_gate) return _gaps; } }

    public string Failure { get { lock (_gate) return _failure; } }

    /// <summary>The longest the stream went quiet - what a watchdog would have to tolerate.</summary>
    public TimeSpan LongestSilence { get { lock (_gate) return _longestSilence; } }

    public IReadOnlyList<RevitPhase> Phases
    {
        get { lock (_gate) return _phases.OrderBy(one => one).ToList(); }
    }

    public IReadOnlyList<string> Captions
    {
        get { lock (_gate) return _captions.ToList(); }
    }

    /// <summary>Every modal dialog Revit raised, by the identifier a policy would key on.</summary>
    public IReadOnlyList<string> Dialogs
    {
        get { lock (_gate) return _dialogs.ToList(); }
    }

    /// <summary>What Revit was doing when it went quiet longest.</summary>
    public RevitPhase SilencePhase { get { lock (_gate) return _silencePhase; } }

    /// <summary>The phase of the last event.</summary>
    public RevitPhase CurrentPhase { get { lock (_gate) return _phase; } }

    /// <summary>
    /// How long since anything was heard, by this machine's clock.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same measurement as <see cref="LongestSilence"/>, which uses Revit's
    /// own timestamps. That one answers "how long was Revit quiet", which is a fact about Revit and
    /// survives a buffer being replayed in one burst. This one answers "how long since we heard
    /// anything", which is the question a waiting caller actually has - and the only one that can be
    /// asked about a Revit that has stopped talking, because a stopped Revit stamps no timestamps.
    /// </remarks>
    public TimeSpan SinceLastHeard { get { lock (_gate) return DateTime.UtcNow - _lastHeardUtc; } }

    /// <summary>The dialog Revit last raised, if it has not visibly moved on since.</summary>
    /// <remarks>
    /// Revit raises nothing when a dialog is dismissed, so "still up" cannot be known directly. What
    /// can be known is whether Revit has done anything since - and the first version cleared this on
    /// the very next event, whatever it was, which loses the dialog the instant anything else
    /// happens while it is still on screen.
    ///
    /// Now it survives until Revit does something that means it is gone: progress, a document
    /// opening or ready, an initialisation, a shutdown. That is a guess either way, and this is the
    /// direction the guess should err - naming a dialog that has been answered costs a confusing
    /// sentence, while forgetting one that is still up costs the diagnosis, which is what happened
    /// on Revit 2027 the first time the watchdog fired.
    ///
    /// The clearing set is every phase that means Revit moved on. <c>Starting</c> and
    /// <c>Unspecified</c> are left out because neither says anything happened. Erring the other way
    /// has its own cost - a dialog raised during startup and answered, followed by a real hang,
    /// would send whoever reads the report looking for a modal window that is no longer on screen.
    /// </remarks>
    public string BlockedBy { get { lock (_gate) return _blockedBy; } }

    private async Task ReadAsync(RevitSideChannel.RevitSideChannelClient client, CancellationToken token)
    {
        try
        {
            using var stream = client.WatchDiagnostics(
                new DiagnosticsRequest { ContractVersion = Handshake.ContractVersion },
                cancellationToken: token);

            while (await stream.ResponseStream.MoveNext(token))
                Record(stream.ResponseStream.Current);
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (Exception error)
        {
            lock (_gate)
                _failure = error.GetType().Name + ": " + error.Message;
        }
    }

    private void Record(DiagnosticEvent evt)
    {
        lock (_gate)
        {
            // Measured from Revit's own clock, not from when the bytes arrived. WatchAsync replays
            // the whole buffer on subscribe, so events minutes apart inside Revit land here
            // back-to-back - which is why the first recording of this number read 0.0s while the
            // sweep it described had waited through a model load. The wire says when we heard;
            // at_unix_ms says when it happened, and the watchdog is about the latter.
            var silence = _lastAtUnixMs == 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(Math.Max(0, evt.AtUnixMs - _lastAtUnixMs));

            if (_received > 0 && silence > _longestSilence)
            {
                _longestSilence = silence;

                // The phase Revit was in while it went quiet, which is what makes the number
                // usable. Silence during Working is Revit doing something long; silence during
                // Blocked is a dialog waiting for a person, and a watchdog must not treat the two
                // the same - the second is not a hang, it is a question nobody answered.
                _silencePhase = _phase;
            }

            _lastAtUnixMs = evt.AtUnixMs;
            _lastHeardUtc = DateTime.UtcNow;
            _received++;
            _dropped += evt.DroppedBefore;
            _phases.Add(evt.Phase);

            if (!evt.ApiThread)
                _offApiThread++;

            // Contiguous unless the publisher dropped something, and it says how many when it did.
            // A gap it did not account for means the stream lost events by itself, which is a
            // different fault and worth telling apart from deliberate thinning.
            if (_lastSequence != 0 && evt.Sequence != _lastSequence + 1 + evt.DroppedBefore)
                _gaps++;

            _lastSequence = evt.Sequence;
            _phase = evt.Phase;

            if (evt.DialogId.Length > 0 && !_dialogs.Contains(evt.DialogId))
                _dialogs.Add(evt.DialogId);

            if (evt.Phase == RevitPhase.Blocked)
                _blockedBy = evt.DialogId;
            else if (evt.Phase is RevitPhase.Working or RevitPhase.DocumentReady or RevitPhase.Idle
                     or RevitPhase.OpeningDocument or RevitPhase.Closing)
                _blockedBy = string.Empty;

            var caption = evt.Caption.Length > 0 ? evt.Caption : evt.Phase.ToString();

            if (!_captions.Contains(caption) && _captions.Count < 24)
                _captions.Add(caption);
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} received, {1} dropped by the publisher, longest quiet spell {2:F1}s during {3}",
                _received, _dropped, _longestSilence.TotalSeconds, _silencePhase);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        // Bounded, because a watcher must never be the reason a sweep hangs - the thing it exists
        // to make visible.
        var finished = false;
        try { finished = _reading.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }

        // Only once nobody is holding its token. Disposing while the reader is still unwinding
        // would raise ObjectDisposedException inside it, which would then be recorded as the
        // stream's failure - after the report had already been read, so it would surface as a
        // mystery in the next run rather than in this one. Leaking one handle for the life of a
        // sweep is the cheaper mistake.
        if (finished)
            _stopping.Dispose();
    }
}
