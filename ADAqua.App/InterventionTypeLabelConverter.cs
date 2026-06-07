using ADAqua.Domain;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ADAqua.App;

public sealed class InterventionTypeLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not InterventionType type)
        {
            return string.Empty;
        }

        var resourceKey = type switch
        {
            InterventionType.WaterChange => "UiInterventionWaterChange",
            InterventionType.Fertilization => "UiInterventionFertilization",
            InterventionType.FilterCleaning => "UiInterventionFilterCleaning",
            InterventionType.PopulationAdded => "UiInterventionPopulationAdded",
            InterventionType.PopulationRemoved => "UiInterventionPopulationRemoved",
            InterventionType.MedicalTreatment => "UiInterventionMedicalTreatment",
            _ => "UiInterventionOther"
        };

        return Application.Current?.MainWindow?.TryFindResource(resourceKey) as string
            ?? type.ToString();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
