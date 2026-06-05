using System.Globalization;
using System.Windows.Controls;

namespace ADAqua.App;

public sealed class WaterParameterRangeValidationRule : ValidationRule
{
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public string ParameterName { get; set; } = "Valeur";

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = Convert.ToString(value, cultureInfo)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.ValidResult;
        }

        if (!TryParseFlexibleDecimal(text, cultureInfo, out var parsedValue))
        {
            return new ValidationResult(false, $"{ParameterName}: valeur numerique invalide.");
        }

        if (parsedValue < Min || parsedValue > Max)
        {
            return new ValidationResult(false, $"{ParameterName}: plage attendue {Min} a {Max}.");
        }

        return ValidationResult.ValidResult;
    }

    private static bool TryParseFlexibleDecimal(string text, CultureInfo cultureInfo, out decimal parsedValue)
    {
        if (decimal.TryParse(text, NumberStyles.Number, cultureInfo, out parsedValue))
        {
            return true;
        }

        var normalized = text.Replace(',', '.');
        if (cultureInfo.NumberFormat.NumberDecimalSeparator == ",")
        {
            normalized = normalized.Replace(".", ",");
        }

        if (decimal.TryParse(normalized, NumberStyles.Number, cultureInfo, out parsedValue))
        {
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out parsedValue);
    }
}
