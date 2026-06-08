using ADAqua.Domain;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ADAqua.App;

public sealed class InventoryMovementTypeLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not InventoryMovementType type)
        {
            return string.Empty;
        }

        var resourceKey = type == InventoryMovementType.Removal
            ? "UiMovementRemoval"
            : "UiMovementAddition";

        return Application.Current?.MainWindow?.TryFindResource(resourceKey) as string
            ?? type.ToString();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
