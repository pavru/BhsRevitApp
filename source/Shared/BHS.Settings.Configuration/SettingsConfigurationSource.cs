using Microsoft.Extensions.Configuration;

namespace BHS.Settings.Configuration;

/// <summary>
/// The layered settings files, as a configuration source.
/// </summary>
/// <remarks>
/// The point of going through <see cref="IConfigurationSource"/> rather than exposing
/// <see cref="ISettings"/> everywhere is that nothing downstream has to know where a value came
/// from: these files and what a companion publishes over the channel are two sources in one
/// builder, and <c>IOptionsMonitor</c>, change tokens and section binding work across both.
/// </remarks>
public sealed class SettingsConfigurationSource : IConfigurationSource
{
    private readonly SettingsOptions? _options;
    private readonly LayeredSettings? _settings;

    /// <summary>Reads the layers described by <paramref name="options"/> and owns them.</summary>
    public SettingsConfigurationSource(SettingsOptions options) => _options = options;

    /// <summary>Uses settings somebody else already read, and does not dispose them.</summary>
    /// <remarks>
    /// The form a host uses when it also reads settings directly - the Win-side host reads a launch
    /// timeout without going through <c>IConfiguration</c> at all - so that both views are the same
    /// object and reload together.
    /// </remarks>
    public SettingsConfigurationSource(LayeredSettings settings) => _settings = settings;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        _settings is not null
            ? new SettingsConfigurationProvider(_settings, owned: false)
            : new SettingsConfigurationProvider(LayeredSettings.Read(_options!), owned: true);
}
