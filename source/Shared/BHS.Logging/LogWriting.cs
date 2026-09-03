using System.Globalization;

namespace BHS.Logging;

/// <summary>
/// The ordinary way to write a line.
/// </summary>
/// <remarks>
/// Every method here does the same two things in the same order: ask whether the level is wanted,
/// and only then build the string. Nothing is formatted for a level nobody is recording.
/// <para>
/// Hence the generic overloads for nought to three arguments rather than <c>params object[]</c>
/// alone. A <c>params</c> array is allocated by the caller <em>before</em> the method is entered,
/// so a disabled <c>Trace</c> call still costs an array and a box per value type. The
/// <c>params</c> form is kept as the fallback for the rare call with more arguments than three,
/// where the shape of the message is the point rather than the cost.
/// </para>
/// <para>
/// Formatting is invariant. A log is read by whoever is diagnosing the machine, often not the
/// person who was sitting at it, and a number that changes its decimal separator with the user's
/// locale is a number nobody can grep for.
/// </para>
/// </remarks>
public static class LogWriting
{
    public static void Trace(this ILog log, string message) => Write(log, LogLevel.Trace, message);

    public static void Trace<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Trace, format, arg0);

    public static void Trace<T0, T1>(this ILog log, string format, T0 arg0, T1 arg1) =>
        Write(log, LogLevel.Trace, format, arg0, arg1);

    public static void Trace<T0, T1, T2>(this ILog log, string format, T0 arg0, T1 arg1, T2 arg2) =>
        Write(log, LogLevel.Trace, format, arg0, arg1, arg2);

    public static void Debug(this ILog log, string message) => Write(log, LogLevel.Debug, message);

    public static void Debug<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Debug, format, arg0);

    public static void Debug<T0, T1>(this ILog log, string format, T0 arg0, T1 arg1) =>
        Write(log, LogLevel.Debug, format, arg0, arg1);

    public static void Debug<T0, T1, T2>(this ILog log, string format, T0 arg0, T1 arg1, T2 arg2) =>
        Write(log, LogLevel.Debug, format, arg0, arg1, arg2);

    public static void Info(this ILog log, string message) => Write(log, LogLevel.Information, message);

    public static void Info<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Information, format, arg0);

    public static void Info<T0, T1>(this ILog log, string format, T0 arg0, T1 arg1) =>
        Write(log, LogLevel.Information, format, arg0, arg1);

    public static void Info<T0, T1, T2>(this ILog log, string format, T0 arg0, T1 arg1, T2 arg2) =>
        Write(log, LogLevel.Information, format, arg0, arg1, arg2);

    public static void Warn(this ILog log, string message) => Write(log, LogLevel.Warning, message);

    public static void Warn<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Warning, format, arg0);

    public static void Warn<T0, T1>(this ILog log, string format, T0 arg0, T1 arg1) =>
        Write(log, LogLevel.Warning, format, arg0, arg1);

    public static void Error(this ILog log, string message) => Write(log, LogLevel.Error, message);

    public static void Error<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Error, format, arg0);

    public static void Error<T0, T1>(this ILog log, string format, T0 arg0, T1 arg1) =>
        Write(log, LogLevel.Error, format, arg0, arg1);

    public static void Critical(this ILog log, string message) => Write(log, LogLevel.Critical, message);

    public static void Critical<T0>(this ILog log, string format, T0 arg0) => Write(log, LogLevel.Critical, format, arg0);

    /// <summary>A failure, with the exception that carried it.</summary>
    public static void Warn(this ILog log, Exception error, string format, params object?[] args) =>
        Write(log, LogLevel.Warning, error, format, args);

    /// <inheritdoc cref="Warn(ILog, Exception, string, object[])"/>
    public static void Error(this ILog log, Exception error, string format, params object?[] args) =>
        Write(log, LogLevel.Error, error, format, args);

    /// <inheritdoc cref="Warn(ILog, Exception, string, object[])"/>
    public static void Critical(this ILog log, Exception error, string format, params object?[] args) =>
        Write(log, LogLevel.Critical, error, format, args);

    /// <summary>More than three arguments. Rare enough that the array is not worth avoiding.</summary>
    public static void Write(this ILog log, LogLevel level, string format, params object?[] args)
    {
        if (log is null || !log.IsEnabled(level))
            return;

        log.Write(level, Format(format, args), null);
    }

    private static void Write(ILog log, LogLevel level, string message)
    {
        if (log is not null && log.IsEnabled(level))
            log.Write(level, message, null);
    }

    private static void Write<T0>(ILog log, LogLevel level, string format, T0 arg0)
    {
        if (log is not null && log.IsEnabled(level))
            log.Write(level, string.Format(CultureInfo.InvariantCulture, format, arg0), null);
    }

    private static void Write<T0, T1>(ILog log, LogLevel level, string format, T0 arg0, T1 arg1)
    {
        if (log is not null && log.IsEnabled(level))
            log.Write(level, string.Format(CultureInfo.InvariantCulture, format, arg0, arg1), null);
    }

    private static void Write<T0, T1, T2>(ILog log, LogLevel level, string format, T0 arg0, T1 arg1, T2 arg2)
    {
        if (log is not null && log.IsEnabled(level))
            log.Write(level, string.Format(CultureInfo.InvariantCulture, format, arg0, arg1, arg2), null);
    }

    private static void Write(ILog log, LogLevel level, Exception error, string format, object?[] args)
    {
        if (log is not null && log.IsEnabled(level))
            log.Write(level, Format(format, args), error);
    }

    /// <summary>
    /// Formats, or says why it could not.
    /// </summary>
    /// <remarks>
    /// A bad format string is a bug in the call site, and the one place it must not surface is
    /// here: an exception thrown while recording a failure loses the failure and replaces it with
    /// a worse one. The malformed message is written instead, which is enough to find the caller.
    /// </remarks>
    private static string Format(string format, object?[] args)
    {
        if (args is null || args.Length == 0)
            return format;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format + "  <- could not be formatted with " + args.Length.ToString(CultureInfo.InvariantCulture) + " argument(s)";
        }
    }
}
