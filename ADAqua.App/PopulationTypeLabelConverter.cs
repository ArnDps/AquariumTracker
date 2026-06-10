using ADAqua.Domain;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ADAqua.App;

public sealed class PopulationTypeLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not PopulationType type)
        {
            return string.Empty;
        }

        var resourceKey = type switch
        {
            PopulationType.Shrimp => "UiPopulationTypeShrimp",
            PopulationType.Snail => "UiPopulationTypeSnail",
            PopulationType.Other => "UiPopulationTypeOther",
            _ => "UiPopulationTypeFish"
        };

        return Application.Current?.MainWindow?.TryFindResource(resourceKey) as string
            ?? type.ToString();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
