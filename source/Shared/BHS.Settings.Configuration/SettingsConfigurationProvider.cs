using Microsoft.Extensions.Configuration;

namespace BHS.Settings.Configuration;

/// <summary>Copies the merged settings into a configuration provider, and again on every reload.</summary>
/// <remarks>
/// A copy rather than a translation: both stores are flat maps of string to string with colons in
/// the keys, so there is nothing to convert. The reload arrives through <c>OnReload</c>, which is
/// the same door the file provider and the peer configuration source use, so change tokens fire
/// the way they would over <c>appsettings.json</c>.
/// </remarks>
internal sealed class SettingsConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly LayeredSettings _settings;
    private readonly bool _owned;

    public SettingsConfigurationProvider(LayeredSettings settings, bool owned)
    {
        _settings = settings;
        _owned = owned;
        _settings.Changed += OnSettingsChanged;
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _settings.Keys)
            data[key] = _settings[key];

        Data = data;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Load();
        OnReload();
    }

    public override string ToString() => "BHS settings (" + _settings.Layers.Count(layer => layer.Exists) + " file(s) read)";

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;

        if (_owned)
            _settings.Dispose();
    }
}
