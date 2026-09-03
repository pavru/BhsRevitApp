using System.Globalization;

namespace BHS.Settings;

/// <summary>
/// Reads single values out of settings, with a stated default.
/// </summary>
/// <remarks>
/// A framework with a dozen settings does not need an object binder; it needs to read a dozen
/// settings. Which is fortunate, because the binder that would do it lives in
/// <c>Microsoft.Extensions.Configuration.Binder</c>, and nothing that goes inside Revit may carry it.
/// <para>
/// A value that is present and unreadable throws rather than falling back. A missing setting is
/// ordinary and the default is the answer; <c>"24O"</c> where a number belongs is a mistake, and
/// quietly running with the default is how a mistake stays hidden for a month.
/// </para>
/// </remarks>
public static class SettingsValues
{
    /// <summary>A string, or the fallback when the key is absent or empty.</summary>
    public static string? Text(this ISettings settings, string key, string? fallback = null)
    {
        var value = settings?[key];
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    public static bool Flag(this ISettings settings, string key, bool fallback)
    {
        var value = settings?[key];

        if (string.IsNullOrEmpty(value))
            return fallback;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        // Not JSON's spelling, but the spelling a person uses in a file they hand-edit.
        if (value is "1" or "yes" or "Yes" or "on" or "On")
            return true;

        if (value is "0" or "no" or "No" or "off" or "Off")
            return false;

        throw Unreadable(key, value!, "true or false");
    }

    public static int Number(this ISettings settings, string key, int fallback)
    {
        var value = settings?[key];

        if (string.IsNullOrEmpty(value))
            return fallback;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw Unreadable(key, value!, "a whole number");
    }

    /// <summary>
    /// A duration, written either as a clock or as a plain number of seconds.
    /// </summary>
    /// <remarks>
    /// Both spellings because both are natural here. Timeouts in this framework are discussed in
    /// seconds - a 240-second shutdown budget - while a schedule reads better as <c>00:04:00</c>.
    /// Refusing one of them would only mean somebody having to look up which.
    /// </remarks>
    public static TimeSpan Duration(this ISettings settings, string key, TimeSpan fallback)
    {
        var value = settings?[key];

        if (string.IsNullOrEmpty(value))
            return fallback;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw Unreadable(key, value!, "seconds, or a duration such as 00:04:00");
    }

    private static InvalidOperationException Unreadable(string key, string value, string expected) =>
        new($"Setting '{key}' is '{value}', which is not {expected}.");
}
