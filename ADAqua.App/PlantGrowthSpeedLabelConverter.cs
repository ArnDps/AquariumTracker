using ADAqua.Domain;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ADAqua.App;

public sealed class PlantGrowthSpeedLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not PlantGrowthSpeed growthSpeed)
        {
            return string.Empty;
        }

        var resourceKey = growthSpeed switch
        {
            PlantGrowthSpeed.Slow => "UiGrowthSlow",
            PlantGrowthSpeed.Fast => "UiGrowthFast",
            _ => "UiGrowthMedium"
        };

        return Application.Current?.MainWindow?.TryFindResource(resourceKey) as string
            ?? growthSpeed.ToString();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
