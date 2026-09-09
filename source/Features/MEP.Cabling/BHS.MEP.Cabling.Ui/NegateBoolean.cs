using System.Globalization;
using System.Windows.Data;

namespace BHS.MEP.Cabling.Ui;

/// <summary>Turns a bool round, for the one place a binding needs the opposite of what it has.</summary>
/// <remarks>
/// The only converter written here. Bool to <c>Visibility</c> is already in WPF as
/// <c>BooleanToVisibilityConverter</c>, and adding our own would mean two of them, one of which
/// somebody eventually changes. There is no such built-in for negation.
/// </remarks>
public sealed class NegateBoolean : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : value;
}
