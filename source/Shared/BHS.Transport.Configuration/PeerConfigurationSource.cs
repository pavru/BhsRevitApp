using Microsoft.Extensions.Configuration;

namespace BHS.Transport.Configuration;

/// <summary>
/// Adds what a companion publishes to a configuration builder, alongside `appsettings.json` and
/// everything else.
/// </summary>
/// <remarks>
/// The point of going through <see cref="IConfigurationSource"/> rather than inventing an API is
/// that nothing downstream has to know where these values came from: <c>IOptionsMonitor</c>,
/// change tokens and section binding all work exactly as they do over a file, because the provider
/// underneath calls <c>OnReload</c> the same way the file provider does.
/// </remarks>
public sealed class PeerConfigurationSource : IConfigurationSource
{
    /// <summary>The companion's pipe, as it announced itself when registering.</summary>
    public string PipeName { get; set; } = string.Empty;

    /// <summary>Section to read, or empty for everything the companion offers.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>How long to wait for the companion's pipe to exist.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = PipeTransport.DefaultConnectionTimeout;

    /// <summary>
    /// Whether a companion that cannot be reached is an error.
    /// </summary>
    /// <remarks>
    /// False by default, and that is the whole reason startup order does not matter: both sides
    /// read their own layered configuration from disk and run without a channel, so a companion
    /// that is not up yet leaves this source empty rather than failing the build. When it does
    /// come up, the first snapshot arrives through the same reload path as any later change.
    /// </remarks>
    public bool Optional { get; set; } = true;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new PeerConfigurationProvider(this);
}
