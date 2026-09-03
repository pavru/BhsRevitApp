using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace BHS.Transport.Configuration;

/// <summary>
/// Keeps a configuration provider in step with what a companion publishes.
/// </summary>
/// <remarks>
/// One streaming call does both jobs: the first snapshot is the initial load, and every later one
/// replaces <c>Data</c> and calls <c>OnReload</c>. That is precisely what the file provider does
/// with <c>reloadOnChange</c>, so everything built on top - <c>IOptionsMonitor</c>, change tokens,
/// rebinding a section - works without knowing a pipe is involved.
/// <para>
/// The ancestor solution computed configuration once in a constructor and had no way to hear about
/// a change at all. This is the part that had to be different.
/// </para>
/// </remarks>
public sealed class PeerConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly PeerConfigurationSource _source;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _watching;
    private bool _disposed;

    public PeerConfigurationProvider(PeerConfigurationSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>The revision of the last snapshot applied, or zero if none has arrived.</summary>
    public ulong Revision { get; private set; }

    public override void Load()
    {
        // Deliberately not blocking on the first snapshot. Waiting here would put the companion's
        // availability on the critical path of building configuration, which is the coupling this
        // design exists to avoid; the first snapshot arrives through the reload path like any
        // other. A caller that genuinely needs a value before proceeding should wait on a change
        // token, where the waiting is visible.
        _watching ??= Task.Run(() => WatchAsync(_stopping.Token));
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = new RevitSideChannel.RevitSideChannelClient(
                    PipeTransport.CreateClient(_source.PipeName, _source.ConnectionTimeout));

                using var call = client.WatchConfiguration(
                    new ConfigurationRequest { Section = _source.Section },
                    cancellationToken: cancellationToken);

                while (await call.ResponseStream.MoveNext(cancellationToken))
                    Apply(call.ResponseStream.Current);
            }
            catch (RpcException) when (!cancellationToken.IsCancellationRequested)
            {
                // The companion is not there, or went away mid-stream. Neither is exceptional: it
                // is a separate process with its own lifetime, and it may well outlive or predate
                // this one several times over.
                if (!_source.Optional)
                    throw;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            // Long enough not to spin against a companion that is not coming back soon, short
            // enough that a restart is picked up without anyone noticing the gap.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Apply(ConfigurationSnapshot snapshot)
    {
        // A revision that did not move means nothing changed - a reconnection to a companion that
        // has not published since. Reloading anyway would wake every change token for nothing.
        if (snapshot.Revision != 0 && snapshot.Revision == Revision)
            return;

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in snapshot.Values)
            data[pair.Key] = pair.Value;

        Data = data;
        Revision = snapshot.Revision;

        OnReload();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
