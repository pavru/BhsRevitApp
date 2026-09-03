namespace BHS.Logging;

/// <summary>How much a record matters.</summary>
/// <remarks>
/// The names and the numbers are those of <c>Microsoft.Extensions.Logging.LogLevel</c>, deliberately
/// and without referencing it. Nothing here may carry that assembly - Revit already has it in six
/// versions at once - but the day a Win-side host wants an <c>ILoggerProvider</c> over this, the
/// bridge is a cast rather than a table. The equivalent translator in the previous generation
/// (<c>..\BHS</c>) is forty-two lines that exist only because the two scales disagreed.
/// </remarks>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,

    /// <summary>Writes nothing. Only meaningful as a threshold.</summary>
    None = 6,
}
