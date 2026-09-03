namespace BHS.Logging;

/// <summary>
/// The static way to reach a log, for the two places that cannot be handed one.
/// </summary>
/// <remarks>
/// Those two places are real and narrow: the first lines of <c>OnStartup</c>, where the container
/// does not exist yet and the most interesting failures happen; and static helpers, which nothing
/// injects into.
/// <para>
/// Everywhere else takes <see cref="ILog"/> through its constructor, like every other dependency.
/// The reason to be strict about it is not taste - it is that on Revit 2024 this static reaches
/// across every add-in in one shared AppDomain, so what it hands back is genuinely shared. That is
/// safe because <see cref="LogRouter.Default"/> is additive, and it stays safe only while the
/// static is used for the two cases above rather than as the normal route.
/// </para>
/// </remarks>
public static class Log
{
    /// <summary>A log for one category, from the shared router.</summary>
    public static ILog For(string category) => LogRouter.Default.For(category);

    /// <summary>A log named after a type.</summary>
    public static ILog For<T>() => LogRouter.Default.For<T>();
}
