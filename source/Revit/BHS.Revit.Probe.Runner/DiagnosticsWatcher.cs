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
/// A connection of its own, because a server stream held open for the length of a sweep would
/// occupy one of the library's four listeners - PoolSize is a hard constant of 4 - and every other
/// check would queue behind it.
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
    private DateTime _lastAt = DateTime.UtcNow;
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
            var now = DateTime.UtcNow;
            var silence = now - _lastAt;

            if (_received > 0 && silence > _longestSilence)
            {
                _longestSilence = silence;

                // The phase Revit was in while it went quiet, which is what makes the number
                // usable. Silence during Working is Revit doing something long; silence during
                // Blocked is a dialog waiting for a person, and a watchdog must not treat the two
                // the same - the second is not a hang, it is a question nobody answered.
                _silencePhase = _phase;
            }

            _lastAt = now;
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
        try { _reading.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }

        _stopping.Dispose();
    }
}
