using BHS.Settings;

namespace BHS.Logging;

/// <summary>A sink that reads its own settings, and reads them again when they change.</summary>
/// <remarks>
/// Separate from <see cref="ILogSink"/> because most sinks have nothing to configure, and a sink
/// written by a feature should not have to implement a method it does not want.
/// <para>
/// Turning a sink off is a level of <see cref="LogLevel.None"/>, never a removal. Removal would
/// break the one rule <see cref="LogRouter"/> has to keep: on Revit 2024 every add-in shares one
/// AppDomain and therefore one router, and whoever configures second must not undo the first.
/// </para>
/// </remarks>
public interface IConfigurableLogSink
{
    void Configure(ISettings settings);
}
