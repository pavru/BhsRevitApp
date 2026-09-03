namespace BHS.Logging;

/// <summary>
/// Where one part of the framework writes what it did.
/// </summary>
/// <remarks>
/// One method, and the convenience lives in <see cref="LogWriting"/>. The same shape as
/// <c>ISettings</c> and <c>SettingsValues</c> next door, and for a sharper reason here: the rule
/// that a message is formatted only after the level has been checked has to hold everywhere, and
/// the way to guarantee that is to leave an implementation no way of getting it wrong.
/// </remarks>
public interface ILog
{
    /// <summary>Whether anything would record a message at this level.</summary>
    /// <remarks>
    /// Asked before the message is built. On <c>net48</c>, in the AppDomain Revit 2024 shares
    /// between every vendor, a formatted string nobody reads is an allocation everyone pays for.
    /// </remarks>
    bool IsEnabled(LogLevel level);

    /// <summary>Records one message. The caller has already decided it is wanted.</summary>
    void Write(LogLevel level, string message, Exception? error);
}
