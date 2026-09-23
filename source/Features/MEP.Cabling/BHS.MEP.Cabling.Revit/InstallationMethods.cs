using BHS.Settings;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// How a project names its installation methods, and which of six fixed slots on a circuit each one
/// is written into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six slots, fixed per project and never reused</b> - the owner's decision of 2026-09-22. The set
/// of methods is open, so a parameter per method cannot exist: a parameter that reached a model
/// cannot be taken back. A slot per circuit, filled in whatever order the circuit met its methods,
/// is what the predecessor did with <c>BhCtCableInstallMode1…5</c>, and it failed exactly here: a
/// schedule cannot sum "the length in slot k where slot k says tray" across circuits whose k differs.
/// Fixing the slot per project makes every column of a schedule mean one method.
/// </para>
/// <code>
/// Model:Cabling:Methods:Parameter = Способ прокладки
/// Model:Cabling:Methods:1         = Лоток
/// Model:Cabling:Methods:2         = Труба
/// </code>
/// <para>
/// <b>Off until the project names the parameter</b>, and off means the model computes exactly what it
/// computed before: slots stay empty and <c>BHS_Cbl_ДлинаПрочая</c> keeps its old meaning - classes
/// other than tray and conduit. Turned on, «прочая» becomes the length laid by no slotted method, so
/// that the slots, the free length and the slack add up to the total.
/// </para>
/// <para>
/// <b>The method comes from the carrier's type</b>, read by name like the catalogue's filter and with
/// the same rule for what a value is - <see cref="TypeParameterText"/>. One parameter for the whole
/// project rather than one per category: a person asks "how is this laid" once, and a project that
/// answered it differently on trays and on conduits would have two schedules to reconcile.
/// </para>
/// </remarks>
public sealed class InstallationMethods
{
    /// <summary>How many slots a circuit has - one pair of parameters each.</summary>
    public const int Slots = 6;

    /// <summary>The section every key of this lives under.</summary>
    public const string MethodsKey = "Model:Cabling:Methods";

    /// <summary>The field naming the carriers' type parameter.</summary>
    public const string ParameterField = "Parameter";

    private readonly string[] _slots;

    /// <param name="parameter">The carriers' type parameter, or empty for off.</param>
    /// <param name="slots">The method named in each slot, first slot first; shorter lists leave the rest empty.</param>
    /// <param name="unreadable">What could not be read, or empty.</param>
    public InstallationMethods(string? parameter, IReadOnlyList<string?>? slots = null, string unreadable = "")
    {
        Parameter = (parameter ?? string.Empty).Trim();
        _slots = new string[Slots];

        for (var i = 0; i < Slots; i++)
            _slots[i] = slots is not null && i < slots.Count ? (slots[i] ?? string.Empty).Trim() : string.Empty;

        Unreadable = unreadable;
    }

    /// <summary>What a project that says nothing gets: off.</summary>
    public static InstallationMethods Off { get; } = new(string.Empty);

    /// <summary>The carriers' type parameter the method is read from, or empty.</summary>
    public string Parameter { get; }

    /// <summary>Whether the project asked for lengths by method at all.</summary>
    public bool IsOn => Parameter.Length != 0;

    /// <summary>A setting under the section that is present and cannot be read, or empty.</summary>
    public string Unreadable { get; }

    /// <summary>The method named in a slot, one-based, or empty when the slot is unused.</summary>
    public string this[int slot] => slot is >= 1 and <= Slots ? _slots[slot - 1] : string.Empty;

    /// <summary>
    /// The slot a method is written into, one-based, or zero when no slot names it - which is also the
    /// answer for a carrier whose type says nothing.
    /// </summary>
    /// <remarks>Without regard to case and trimmed, as the carrier's value is: two people typed them.</remarks>
    public int SlotOf(string? method)
    {
        var name = (method ?? string.Empty).Trim();

        if (name.Length == 0)
            return 0;

        for (var i = 0; i < Slots; i++)
        {
            if (string.Equals(_slots[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return 0;
    }

    /// <summary>Reads the project's methods.</summary>
    /// <remarks>
    /// Soft, like the rest of the cabling settings: a slot number out of range or a method named in two
    /// slots is reported and skipped, never a reason to stop the command. A method named twice keeps
    /// the first slot - the second would split its length between two columns of one schedule.
    /// </remarks>
    public static InstallationMethods Read(ISettings model)
    {
        var section = model.Section(MethodsKey);
        var parameter = section[ParameterField];
        var slots = new string?[Slots];
        var unreadable = new List<string>();

        foreach (var key in section.Keys)
        {
            // Only the first segment: a key nested under a slot is not something this reads, and is
            // named rather than guessed at.
            var name = key.Trim();

            if (string.Equals(name, ParameterField, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var slot)
                || slot < 1 || slot > Slots)
            {
                unreadable.Add(MethodsKey + ":" + name + " - slots are 1 to " + Slots);
                continue;
            }

            var method = (section[name] ?? string.Empty).Trim();

            if (method.Length == 0)
                continue;

            var twice = Array.FindIndex(slots, one => string.Equals(one, method, StringComparison.OrdinalIgnoreCase));

            if (twice >= 0)
            {
                unreadable.Add(MethodsKey + ":" + name + " = '" + method + "' - already slot " + (twice + 1));
                continue;
            }

            slots[slot - 1] = method;
        }

        return new InstallationMethods(parameter, slots, string.Join("; ", unreadable));
    }
}
