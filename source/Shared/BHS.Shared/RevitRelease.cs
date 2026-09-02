namespace BHS.Shared;

/// <summary>
/// A Revit release, expressed as the year Autodesk names it by. Holding this here rather than in
/// a Revit-side assembly lets Win-side code talk about releases without referencing the API.
/// </summary>
/// <remarks>
/// Written as a plain readonly struct rather than a record struct on purpose: this assembly
/// targets net48 for Revit 2024, and record structs need <c>IsExternalInit</c>, which .NET
/// Framework does not have. Language version alone does not make that syntax usable here.
/// </remarks>
public readonly struct RevitRelease : IEquatable<RevitRelease>, IComparable<RevitRelease>
{
    /// <summary>The oldest release this framework supports.</summary>
    public const int EarliestSupported = 2024;

    public RevitRelease(int year)
    {
        if (year < EarliestSupported)
            throw new ArgumentOutOfRangeException(
                nameof(year), year, $"Revit {year} is older than the supported range, which starts at {EarliestSupported}.");

        Year = year;
    }

    public int Year { get; }

    /// <summary>
    /// Reads a release from the value Revit reports as its version number, such as "2026".
    /// </summary>
    public static RevitRelease Parse(string text) =>
        TryParse(text, out var release)
            ? release
            : throw new FormatException($"'{text}' is not a supported Revit release.");

    public static bool TryParse(string? text, out RevitRelease release)
    {
        if (int.TryParse(text, out var year) && year >= EarliestSupported)
        {
            release = new RevitRelease(year);
            return true;
        }

        release = default;
        return false;
    }

    public bool Equals(RevitRelease other) => Year == other.Year;

    public override bool Equals(object? obj) => obj is RevitRelease other && Equals(other);

    public override int GetHashCode() => Year;

    public int CompareTo(RevitRelease other) => Year.CompareTo(other.Year);

    public override string ToString() => Year.ToString();

    public static bool operator ==(RevitRelease left, RevitRelease right) => left.Equals(right);

    public static bool operator !=(RevitRelease left, RevitRelease right) => !left.Equals(right);

    public static bool operator <(RevitRelease left, RevitRelease right) => left.Year < right.Year;

    public static bool operator >(RevitRelease left, RevitRelease right) => left.Year > right.Year;

    public static bool operator <=(RevitRelease left, RevitRelease right) => left.Year <= right.Year;

    public static bool operator >=(RevitRelease left, RevitRelease right) => left.Year >= right.Year;
}
