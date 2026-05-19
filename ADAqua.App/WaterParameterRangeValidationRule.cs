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

        if (!decimal.TryParse(text, NumberStyles.Number, cultureInfo, out var parsedValue))
        {
            return new ValidationResult(false, $"{ParameterName}: valeur numerique invalide.");
        }

        if (parsedValue < Min || parsedValue > Max)
        {
            return new ValidationResult(false, $"{ParameterName}: plage attendue {Min} a {Max}.");
        }

        return ValidationResult.ValidResult;
    }
}
