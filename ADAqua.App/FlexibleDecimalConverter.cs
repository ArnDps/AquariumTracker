using System.Globalization;
using System.Windows.Data;

namespace ADAqua.App;

public sealed class FlexibleDecimalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is decimal decimalValue)
        {
            return decimalValue.ToString(culture);
        }

        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return IsNullableDecimal(targetType) ? null : Binding.DoNothing;
        }

        // Keep transient decimal input states (ex: "0," / "0.") so the user can continue typing.
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        if (text.EndsWith(decimalSeparator, StringComparison.Ordinal)
            || text.EndsWith(",", StringComparison.Ordinal)
            || text.EndsWith(".", StringComparison.Ordinal))
        {
            return Binding.DoNothing;
        }

        if (TryParseFlexible(text, culture, out var parsed))
        {
            return parsed;
        }

        return Binding.DoNothing;
    }

    private static bool TryParseFlexible(string text, CultureInfo culture, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Number, culture, out value))
        {
            return true;
        }

        var normalized = text.Replace(',', '.');
        if (culture.NumberFormat.NumberDecimalSeparator == ",")
        {
            normalized = normalized.Replace(".", ",");
        }

        if (decimal.TryParse(normalized, NumberStyles.Number, culture, out value))
        {
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsNullableDecimal(Type targetType)
    {
        return targetType == typeof(decimal?) || Nullable.GetUnderlyingType(targetType) == typeof(decimal);
    }
}
