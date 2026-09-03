using Microsoft.Extensions.Configuration;

namespace BHS.Settings.Configuration;

/// <summary>Adds the layered settings files to a configuration builder.</summary>
public static class SettingsConfigurationExtensions
{
    /// <summary>Reads the layers for one side and adds them.</summary>
    public static IConfigurationBuilder AddBhsSettings(this IConfigurationBuilder builder, SettingsOptions options)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        return builder.Add(new SettingsConfigurationSource(options));
    }

    /// <summary>Adds settings that have already been read, sharing them rather than reading twice.</summary>
    public static IConfigurationBuilder AddBhsSettings(this IConfigurationBuilder builder, LayeredSettings settings)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        return builder.Add(new SettingsConfigurationSource(settings));
    }
}
