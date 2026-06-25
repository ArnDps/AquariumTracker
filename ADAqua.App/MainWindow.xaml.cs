using ADAqua.Domain;
using ADAqua.Infrastructure;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ADAqua.App;

public partial class MainWindow : Window
{
    private const string LanguageFrench = "fr";
    private const string LanguageEnglish = "en";
    private const string LanguageGerman = "de";
    private const string ThemeLight = "light";
    private const string ThemeDark = "dark";
    private const string FontSizeSmall = "small";
    private const string FontSizeNormal = "normal";
    private const string FontSizeLarge = "large";
    private const string DensityCompact = "compact";
    private const string DensityComfortable = "comfortable";
    private const string AccentTeal = "teal";
    private const string AccentBlue = "blue";
    private const string AccentGreen = "green";
    private const string AccentPurple = "purple";

    private readonly MainWindowViewModel viewModel = new();
    private readonly SemaphoreSlim selectedAquariumPersistGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string>> localizedTexts = CreateLocalizedTexts();
    private MySqlAquariumRepository? repository;
    private string? activeConnectionString;
    private bool isApplyingSettings;
    private bool isLoadingAquariums;
    private bool isInlineGridPersistQueued;
    private bool isInlineGridPersisting;
    private bool isClassificationPersisting;
    private string currentLanguage = LanguageFrench;
    private string currentTheme = ThemeLight;
    private string currentFontSize = FontSizeNormal;
    private string currentDensity = DensityComfortable;
    private string currentAccentColor = AccentTeal;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetTextProvider(T);
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        viewModel.SelectedAquariumWaterTypeChanged += ViewModelOnSelectedAquariumWaterTypeChanged;
        viewModel.SelectedAquariumContainerTypeChanged += ViewModelOnSelectedAquariumWaterTypeChanged;
        AppLogger.Info("Application started.");

        var appSettings = AppSettingsStore.Load();
        var startupLanguage = NormalizeLanguageCode(appSettings?.LanguageCode);
        var startupTheme = NormalizeThemeCode(appSettings?.ThemeCode);
        var startupFontSize = NormalizeFontSizeCode(appSettings?.FontSizeCode);
        var startupDensity = NormalizeDensityCode(appSettings?.DensityCode);
        var startupAccentColor = NormalizeAccentColorCode(appSettings?.AccentColorCode);

        currentAccentColor = startupAccentColor;
        ApplyTheme(startupTheme);
        ApplyFontSize(startupFontSize);
        ApplyDensity(startupDensity);
        ApplyLanguage(startupLanguage);
        ApplyWaterParameterValidationRanges();

        isApplyingSettings = true;
        SelectComboByTag(LanguageComboBox, startupLanguage);
        SelectComboByTag(ThemeComboBox, startupTheme);
        SelectComboByTag(FontSizeComboBox, startupFontSize);
        SelectComboByTag(DensityComboBox, startupDensity);
        SelectComboByTag(AccentColorComboBox, startupAccentColor);
        isApplyingSettings = false;

        var resolved = ResolveConnectionConfiguration();
        if (!string.IsNullOrWhiteSpace(resolved.ConnectionString))
        {
            ApplyConnectionString(resolved.ConnectionString, resolved.MessageKey);
        }
        else
        {
            viewModel.StatusMessage = T("StatusMySqlNotConfigured");
            AppLogger.Info("MySQL not configured at startup.");
        }

        RefreshApplicationLog();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StatusMessage) && !string.IsNullOrWhiteSpace(viewModel.StatusMessage))
        {
            AppLogger.Info($"STATUS: {viewModel.StatusMessage}");
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedAquarium))
        {
            ApplyWaterParameterValidationRanges();
        }
    }

    private async void ViewModelOnSelectedAquariumWaterTypeChanged(object? sender, EventArgs e)
    {
        ApplyWaterParameterValidationRanges();
        await PersistSelectedAquariumClassificationAsync();
    }

    private static (string? ConnectionString, string MessageKey) ResolveConnectionConfiguration()
    {
        var savedSettings = MySqlConfigurationStore.Load();
        if (savedSettings is not null)
        {
            return (savedSettings.BuildConnectionString(), "StatusMySqlConfiguredSecure");
        }

        var environmentConnectionString = Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Machine);

        return string.IsNullOrWhiteSpace(environmentConnectionString)
            ? (null, string.Empty)
            : (environmentConnectionString, "StatusMySqlConfiguredEnv");
    }

    private void ApplyConnectionString(string connectionString, string messageKey)
    {
        activeConnectionString = connectionString;
        repository = new MySqlAquariumRepository(connectionString);
        viewModel.StatusMessage = T(messageKey);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            return;
        }

        try
        {
            await repository.InitializeAsync();
            await LoadAquariumsAsync();
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusReadFailed")} {exception.Message}";
            AppLogger.Error("Load on startup failed.", exception);
        }
    }

    private async void ConfigureDatabase_Click(object sender, RoutedEventArgs e)
    {
        var settings = MySqlConfigurationStore.CreateDefault(activeConnectionString);
        var configurationWindow = new MySqlConfigurationWindow(settings)
        {
            Owner = this
        };

        if (configurationWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(configurationWindow.ConnectionString))
        {
            ApplyConnectionString(configurationWindow.ConnectionString, "StatusMySqlConfigSaved");
            try
            {
                await LoadAquariumsAsync();
            }
            catch (Exception exception)
            {
                viewModel.StatusMessage = $"{T("StatusConfigSavedReadFailed")} {exception.Message}";
                AppLogger.Error("Reload after MySQL config failed.", exception);
            }
        }
    }

    private async void InitializeDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = T("StatusConfigureBeforeInit");
            return;
        }

        try
        {
            await repository.InitializeAsync();
            viewModel.StatusMessage = T("StatusSchemaInitialized");
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusInitializationFailed")} {exception.Message}";
            AppLogger.Error("Database initialization failed.", exception);
        }
    }

    private async Task LoadAquariumsAsync(Guid? selectedAquariumId = null)
    {
        if (repository is null)
        {
            return;
        }

        isLoadingAquariums = true;
        try
        {
            var aquariums = await repository.GetAllAsync();
            var plantReferences = await repository.GetPlantReferencesAsync();
            var animalReferences = await repository.GetAnimalReferencesAsync();
            viewModel.SetPlantReferences(plantReferences);
            viewModel.SetAnimalReferences(animalReferences);
            viewModel.ReplaceAquariums(aquariums);
            if (selectedAquariumId is not null)
            {
                viewModel.SelectAquarium(selectedAquariumId.Value);
            }

            AppLogger.Info(
                $"Loaded selected aquarium classification: ContainerType={viewModel.SelectedAquarium.ContainerType}, WaterType={viewModel.SelectedAquarium.WaterType}, SelectedWaterType={viewModel.SelectedAquariumWaterType}.");

            viewModel.StatusMessage = aquariums.Count == 0
                ? T("StatusConnectedNoAquarium")
                : string.Format(T("StatusConnectedAquariumCount"), aquariums.Count);
        }
        finally
        {
            isLoadingAquariums = false;
        }
    }

    private async Task ReloadPlantReferencesAsync()
    {
        if (repository is null)
        {
            return;
        }

        var plantReferences = await repository.GetPlantReferencesAsync();
        viewModel.SetPlantReferences(plantReferences);
    }

    private async Task ReloadAnimalReferencesAsync()
    {
        if (repository is null)
        {
            return;
        }

        var animalReferences = await repository.GetAnimalReferencesAsync();
        viewModel.SetAnimalReferences(animalReferences);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = T("StatusLocalModeConfigure");
            return;
        }

        if (!CommitPendingEdits())
        {
            viewModel.StatusMessage = T("StatusSaveInvalidInput");
            return;
        }

        try
        {
            var aquariumId = viewModel.SelectedAquarium.Id;
            await repository.InitializeAsync();
            await repository.SaveAsync(viewModel.SelectedAquarium);
            await LoadAquariumsAsync(aquariumId);
            viewModel.StatusMessage = T("StatusAquariumSaved");
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusSaveFailed")} {exception.Message}";
            AppLogger.Error("Save button failed.", exception);
        }
    }

    private void ApplyWaterParameterValidationRanges()
    {
        var marine = viewModel.IsSelectedAquariumMarine;
        var pond = viewModel.IsSelectedContainerFishPond;
        SetValidationRange("AmmoniaRangeRule", 0m, marine ? 0.2m : 0.5m);
        SetValidationRange("NitritesRangeRule", 0m, marine ? 0.1m : 0.2m);
        SetValidationRange("NitratesRangeRule", 0m, pond ? 120m : marine ? 60m : 100m);
        SetValidationRange("PhRangeRule", pond ? 6.5m : marine ? 7.6m : 6m, pond ? 9m : marine ? 8.6m : 8.5m);
        SetValidationRange("GhRangeRule", pond ? 1m : marine ? 6m : 1m, pond ? 25m : marine ? 30m : 20m);
        SetValidationRange("KhRangeRule", pond ? 3m : marine ? 6m : 0m, pond ? 18m : marine ? 14m : 15m);
        SetValidationRange("TemperatureRangeRule", pond ? 0m : marine ? 22m : 18m, pond ? 32m : marine ? 30m : 30m);
    }

    private void SetValidationRange(string resourceKey, decimal min, decimal max)
    {
        if (Resources[resourceKey] is WaterParameterRangeValidationRule rule)
        {
            rule.Min = min;
            rule.Max = max;
        }
    }

    private bool CommitPendingEdits()
    {
        Keyboard.ClearFocus();

        foreach (var dataGrid in FindVisualChildren<DataGrid>(this))
        {
            dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            dataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (Validation.GetHasError(dataGrid))
            {
                return false;
            }
        }

        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            FocusCurrentSearchBox();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            Save_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D && MainTabControl.SelectedIndex == 1)
        {
            DuplicateMeasurement_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            viewModel.ClearGridFilters();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && !IsTextEntryFocused())
        {
            DeleteCurrentGridSelection();
            e.Handled = true;
        }
    }

    private void FocusCurrentSearchBox()
    {
        var target = MainTabControl.SelectedIndex switch
        {
            1 => MeasurementSearchBox,
            3 => PlantSearchBox,
            4 => PopulationSearchBox,
            5 => InterventionSearchBox,
            _ => null
        };

        if (target is null)
        {
            return;
        }

        target.Focus();
        target.SelectAll();
    }

    private void DeleteCurrentGridSelection()
    {
        switch (MainTabControl.SelectedIndex)
        {
            case 1:
                DeleteMeasurement_Click(this, new RoutedEventArgs());
                break;
            case 3:
                DeletePlant_Click(this, new RoutedEventArgs());
                break;
            case 4:
                DeletePopulation_Click(this, new RoutedEventArgs());
                break;
            case 5:
                DeleteIntervention_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private static bool IsTextEntryFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase or ComboBox or DatePicker;
    }

    private void ClearGridFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ClearGridFilters();
    }

    private static IEnumerable<TChild> FindVisualChildren<TChild>(DependencyObject root) where TChild : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TChild typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindVisualChildren<TChild>(child))
            {
                yield return nested;
            }
        }
    }

    private async void NewAquarium_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddAquarium();
        await PersistSelectedAquariumAsync(T("StatusNewAquariumSaved"));
    }

    private async void DeleteAquarium_Click(object sender, RoutedEventArgs e)
    {
        var aquarium = viewModel.SelectedAquarium;
        var result = MessageBox.Show(
            string.Format(T("ConfirmDeleteAquarium"), aquarium.Name),
            T("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (repository is not null)
            {
                await repository.DeleteAsync(aquarium.Id);
            }

            viewModel.DeleteSelectedAquarium();
            viewModel.StatusMessage = T("StatusAquariumDeleted");
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusAquariumDeleteFailed")} {exception.Message}";
            AppLogger.Error("Delete aquarium failed.", exception);
        }
    }

    private async void AddMeasurement_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddMeasurement();
        await PersistSelectedAquariumAsync(T("StatusMeasurementSaved"));
    }

    private async void DuplicateMeasurement_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.DuplicateSelectedMeasurement())
        {
            viewModel.StatusMessage = T("StatusSelectMeasurementDuplicate");
            return;
        }

        await PersistSelectedAquariumAsync(T("StatusMeasurementDuplicated"));
    }

    private async void DeleteMeasurement_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedMeasurement is null)
        {
            viewModel.StatusMessage = T("StatusSelectMeasurementDelete");
            return;
        }

        var result = MessageBox.Show(
            string.Format(T("ConfirmDeleteMeasurement"), viewModel.SelectedMeasurement.MeasuredAt),
            T("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedMeasurement();
            await PersistSelectedAquariumAsync(T("StatusMeasurementDeleted"));
        }
    }

    private async void AddPlant_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPlant();
        await PersistSelectedAquariumAsync(T("StatusPlantSaved"));
    }

    private async void DeletePlant_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPlant is null)
        {
            viewModel.StatusMessage = T("StatusSelectPlantDelete");
            return;
        }

        var result = MessageBox.Show(
            string.Format(T("ConfirmDeletePlant"), viewModel.SelectedPlant.CommonName),
            T("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedPlant();
            await PersistSelectedAquariumAsync(T("StatusPlantDeleted"));
        }
    }

    private async void AddPopulation_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPopulation();
        await PersistSelectedAquariumAsync(T("StatusPopulationSaved"));
    }

    private async void DeletePopulation_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPopulation is null)
        {
            viewModel.StatusMessage = T("StatusSelectPopulationDelete");
            return;
        }

        var result = MessageBox.Show(
            string.Format(T("ConfirmDeletePopulation"), viewModel.SelectedPopulation.CommonName),
            T("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedPopulation();
            await PersistSelectedAquariumAsync(T("StatusPopulationDeleted"));
        }
    }

    private async void AddIntervention_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.TryAddIntervention())
        {
            viewModel.StatusMessage = T("StatusInterventionInvalidTime");
            return;
        }

        await PersistSelectedAquariumAsync(T("StatusInterventionSaved"));
    }

    private async void DeleteIntervention_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedIntervention is null)
        {
            viewModel.StatusMessage = T("StatusSelectInterventionDelete");
            return;
        }

        var result = MessageBox.Show(
            string.Format(T("ConfirmDeleteIntervention"), viewModel.SelectedIntervention.OccurredAt),
            T("ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedIntervention();
            await PersistSelectedAquariumAsync(T("StatusInterventionDeleted"));
        }
    }

    private void MeasurementGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusMeasurementSaved"), "Measurement inline edit persist failed.", viewModel.RefreshMeasurementsAfterEdit);
        }
    }

    private void MeasurementGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusMeasurementSaved"), "Measurement row edit persist failed.", viewModel.RefreshMeasurementsAfterEdit);
        }
    }

    private void PlantGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPlantSaved"), "Plant inline edit persist failed.", viewModel.RefreshPlantInventoryAfterEdit);
        }
    }

    private void PlantGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPlantSaved"), "Plant row edit persist failed.", viewModel.RefreshPlantInventoryAfterEdit);
        }
    }

    private void PopulationGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPopulationSaved"), "Population inline edit persist failed.", viewModel.RefreshPopulationInventoryAfterEdit);
        }
    }

    private void PopulationGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPopulationSaved"), "Population row edit persist failed.", viewModel.RefreshPopulationInventoryAfterEdit);
        }
    }

    private void InterventionGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusInterventionSaved"), "Intervention inline edit persist failed.", viewModel.RefreshInterventionsAfterEdit);
        }
    }

    private void InterventionGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusInterventionSaved"), "Intervention row edit persist failed.", viewModel.RefreshInterventionsAfterEdit);
        }
    }

    private void QueueInlineGridPersist(string successMessage, string logContext, Action? beforePersist = null)
    {
        if (isInlineGridPersistQueued)
        {
            return;
        }

        isInlineGridPersistQueued = true;
        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                isInlineGridPersistQueued = false;
                beforePersist?.Invoke();
                await PersistInlineGridEditAsync(successMessage, logContext);
            }),
            DispatcherPriority.ContextIdle);
    }

    private async Task PersistInlineGridEditAsync(string successMessage, string logContext)
    {
        if (isInlineGridPersisting)
        {
            return;
        }

        if (repository is null)
        {
            viewModel.StatusMessage = T("StatusLocalModeConfigure");
            return;
        }

        if (HasValidationErrors())
        {
            viewModel.StatusMessage = T("StatusSaveInvalidInput");
            return;
        }

        try
        {
            isInlineGridPersisting = true;
            await repository.InitializeAsync();
            await repository.SaveAsync(viewModel.SelectedAquarium);
            viewModel.StatusMessage = successMessage;
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusSaveFailed")} {exception.Message}";
            AppLogger.Error(logContext, exception);
        }
        finally
        {
            isInlineGridPersisting = false;
        }
    }

    private bool HasValidationErrors()
    {
        if (Validation.GetHasError(this))
        {
            return true;
        }

        return FindVisualChildren<DependencyObject>(this).Any(Validation.GetHasError);
    }

    private async Task PersistSelectedAquariumAsync(string successMessage)
    {
        if (repository is null)
        {
            return;
        }

        if (!CommitPendingEdits())
        {
            viewModel.StatusMessage = T("StatusSaveInvalidInput");
            return;
        }

        try
        {
            var aquariumId = viewModel.SelectedAquarium.Id;
            await repository.InitializeAsync();
            await repository.SaveAsync(viewModel.SelectedAquarium);
            await LoadAquariumsAsync(aquariumId);
            viewModel.StatusMessage = successMessage;
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusSaveFailed")} {exception.Message}";
            AppLogger.Error("Auto-persist action failed.", exception);
        }
    }

    private async void SheetField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || isLoadingAquariums)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        await PersistSelectedAquariumSheetAsync();
    }

    private async void SheetDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || isLoadingAquariums)
        {
            return;
        }

        if (sender is DatePicker datePicker)
        {
            datePicker.GetBindingExpression(DatePicker.SelectedDateProperty)?.UpdateSource();
        }

        await PersistSelectedAquariumSheetAsync();
    }

    private async void SheetClassificationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || isLoadingAquariums)
        {
            return;
        }

        if (sender is ComboBox comboBox)
        {
            comboBox.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
        }

        ApplyWaterParameterValidationRanges();
        await PersistSelectedAquariumClassificationAsync();
    }

    private async Task PersistSelectedAquariumSheetAsync()
    {
        if (repository is null)
        {
            viewModel.StatusMessage = T("StatusLocalModeConfigure");
            return;
        }

        if (HasValidationErrors())
        {
            viewModel.StatusMessage = T("StatusSaveInvalidInput");
            return;
        }

        var aquarium = viewModel.SelectedAquarium;
        await selectedAquariumPersistGate.WaitAsync();
        try
        {
            AppLogger.Info($"Saving selected aquarium sheet: AquariumId={aquarium.Id}, VolumeLiters={aquarium.VolumeLiters}.");
            await repository.SaveSheetAsync(aquarium);
            viewModel.StatusMessage = T("StatusAquariumSaved");
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusSaveFailed")} {exception.Message}";
            AppLogger.Error("Sheet persist failed.", exception);
        }
        finally
        {
            selectedAquariumPersistGate.Release();
        }
    }

    private async Task PersistSelectedAquariumClassificationAsync()
    {
        if (isClassificationPersisting)
        {
            return;
        }

        if (repository is null)
        {
            viewModel.StatusMessage = T("StatusLocalModeConfigure");
            return;
        }

        var aquarium = viewModel.SelectedAquarium;
        var aquariumId = aquarium.Id;
        aquarium.ContainerType = viewModel.SelectedAquariumContainerType;
        aquarium.WaterType = viewModel.SelectedAquariumWaterType;

        isClassificationPersisting = true;
        await selectedAquariumPersistGate.WaitAsync();
        try
        {
            AppLogger.Info($"Saving selected aquarium classification: AquariumId={aquariumId}, ContainerType={aquarium.ContainerType}, WaterType={aquarium.WaterType}.");
            await repository.SaveSheetAsync(aquarium);
            viewModel.RefreshSelectedAquariumClassificationAfterPersist();
            viewModel.StatusMessage = T("StatusAquariumSaved");
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusSaveFailed")} {exception.Message}";
            AppLogger.Error("Classification persist failed.", exception);
        }
        finally
        {
            selectedAquariumPersistGate.Release();
            isClassificationPersisting = false;
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string languageCode)
        {
            return;
        }

        ApplyLanguage(languageCode);
        SaveCurrentAppSettings();
        viewModel.StatusMessage = T("StatusLanguageChanged");
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string themeCode)
        {
            return;
        }

        ApplyTheme(themeCode);
        SaveCurrentAppSettings();
        viewModel.StatusMessage = T("StatusThemeChanged");
    }

    private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || FontSizeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string fontSizeCode)
        {
            return;
        }

        ApplyFontSize(fontSizeCode);
        SaveCurrentAppSettings();
        viewModel.StatusMessage = T("StatusAppearanceChanged");
    }

    private void DensityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || DensityComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string densityCode)
        {
            return;
        }

        ApplyDensity(densityCode);
        SaveCurrentAppSettings();
        viewModel.StatusMessage = T("StatusAppearanceChanged");
    }

    private void AccentColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || AccentColorComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string accentColorCode)
        {
            return;
        }

        currentAccentColor = NormalizeAccentColorCode(accentColorCode);
        ApplyTheme(currentTheme);
        SaveCurrentAppSettings();
        viewModel.StatusMessage = T("StatusAppearanceChanged");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveCurrentAppSettings();
    }

    private void SaveCurrentAppSettings()
    {
        try
        {
            AppSettingsStore.Save(new AppSettings
            {
                LanguageCode = currentLanguage,
                ThemeCode = currentTheme,
                FontSizeCode = currentFontSize,
                DensityCode = currentDensity,
                AccentColorCode = currentAccentColor
            });
        }
        catch (IOException)
        {
            AppLogger.Error("App settings save failed (IO).");
        }
        catch (UnauthorizedAccessException)
        {
            AppLogger.Error("App settings save failed (Unauthorized).");
        }
    }

    private async void PlantReferenceSearch_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant la recherche automatique.";
            return;
        }

        var minimumParameterGroups = ShowReferenceSearchCriteriaDialog();
        if (!minimumParameterGroups.HasValue)
        {
            viewModel.StatusMessage = "Recherche plantes annulée.";
            return;
        }

        try
        {
            await repository.InitializeAsync();
            var lastProgressMessage = string.Empty;
            var progress = new Progress<string>(message =>
            {
                lastProgressMessage = message;
                viewModel.StatusMessage = message;
            });
            viewModel.StatusMessage = $"Recherche plantes lancée avec au moins {minimumParameterGroups.Value} groupes de paramètres.";
            var imported = await repository.ImportPlantReferencesFromWebAsync(progress, minimumParameterGroups.Value);
            await ReloadPlantReferencesAsync();
            viewModel.StatusMessage = string.IsNullOrWhiteSpace(lastProgressMessage)
                ? $"{imported} nouvelles plantes importées depuis le web."
                : lastProgressMessage;
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Import web des plantes impossible: {ex.Message}";
            AppLogger.Error("Plant import failed.", ex);
        }
    }

    private void PlantReferenceCompatibility_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyPlantReferenceCompatibilityHighlight();
        viewModel.StatusMessage = "Compatibilité plantes évaluée sur la derniere mesure du contenant selectionne.";
    }

    private void PlantReferenceApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyPlantReferenceFilters();
        viewModel.StatusMessage = "Filtres plantes appliqués.";
    }

    private void PlantReferenceResetFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ResetPlantReferenceFilters();
        viewModel.StatusMessage = "Filtres plantes réinitialisés.";
    }

    private async void PlantReferenceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant suppression.";
            return;
        }

        if (viewModel.SelectedPlantReference is null)
        {
            viewModel.StatusMessage = "Sélectionner une référence plante à supprimer.";
            return;
        }

        var reference = viewModel.SelectedPlantReference;
        var result = MessageBox.Show(
            $"Supprimer la référence \"{reference.ScientificName}\" ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeletePlantReferenceAsync(reference.Id);
            await ReloadPlantReferencesAsync();
            viewModel.StatusMessage = "Référence plante supprimée.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Suppression référence impossible: {ex.Message}";
            AppLogger.Error("Delete plant reference failed.", ex);
        }
    }

    private async void PlantReferenceReset_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant réinitialisation.";
            return;
        }

        var result = MessageBox.Show(
            "Réinitialiser le référentiel plantes ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var before = await repository.GetPlantReferenceCountAsync();
            await repository.ResetPlantReferencesAsync();
            var after = await repository.GetPlantReferenceCountAsync();
            await ReloadPlantReferencesAsync();
            viewModel.StatusMessage = $"Référentiel plantes réinitialisé (avant: {before}, après: {after}).";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Réinitialisation plantes impossible: {ex.Message}";
            AppLogger.Error("Reset plant references failed.", ex);
        }
    }

    private void PlantReferencesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not PlantReferenceItem item || string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.SourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Ouverture du lien impossible: {ex.Message}";
            AppLogger.Error("Open plant reference URL failed.", ex);
        }
    }

    private async void AnimalReferenceSearch_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant la recherche automatique.";
            return;
        }

        var minimumParameterGroups = ShowReferenceSearchCriteriaDialog();
        if (!minimumParameterGroups.HasValue)
        {
            viewModel.StatusMessage = "Recherche espèces annulée.";
            return;
        }

        try
        {
            await repository.InitializeAsync();
            var lastProgressMessage = string.Empty;
            var progress = new Progress<string>(message =>
            {
                lastProgressMessage = message;
                viewModel.StatusMessage = message;
            });
            viewModel.StatusMessage = $"Recherche espèces lancée avec au moins {minimumParameterGroups.Value} groupes de paramètres.";
            var imported = await repository.ImportAnimalReferencesFromWebAsync(progress, minimumParameterGroups.Value);
            await ReloadAnimalReferencesAsync();
            viewModel.StatusMessage = string.IsNullOrWhiteSpace(lastProgressMessage)
                ? $"{imported} nouvelles espèces importées depuis le web."
                : lastProgressMessage;
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Import web des espèces impossible: {ex.Message}";
            AppLogger.Error("Animal import failed.", ex);
        }
    }

    private int? ShowReferenceSearchCriteriaDialog()
    {
        const int defaultMinimumParameterGroups = 4;
        var selectedMinimum = defaultMinimumParameterGroups;
        var textBrush = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var secondaryBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.DimGray;
        var backgroundBrush = TryFindResource("CardBackgroundBrush") as Brush ?? Brushes.White;
        var inputBrush = TryFindResource("InputBackgroundBrush") as Brush ?? Brushes.White;
        var borderBrush = TryFindResource("CardBorderBrush") as Brush ?? Brushes.LightGray;

        var input = new TextBox
        {
            Text = defaultMinimumParameterGroups.ToString(),
            Width = 80,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = inputBrush,
            Foreground = textBrush,
            BorderBrush = borderBrush
        };

        var errorText = new TextBlock
        {
            Text = T("UiReferenceSearchInvalidMinimum"),
            Foreground = Brushes.OrangeRed,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var dialog = new Window
        {
            Title = T("UiReferenceSearchCriteriaTitle"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = backgroundBrush,
            Foreground = textBrush
        };

        var okButton = new Button
        {
            Content = T("UiDialogOk"),
            Width = 90,
            Height = 32,
            IsDefault = true
        };
        var cancelButton = new Button
        {
            Content = T("UiDialogCancel"),
            Width = 90,
            Height = 32,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            if (int.TryParse(input.Text.Trim(), out var parsed) && parsed is >= 1 and <= 8)
            {
                selectedMinimum = parsed;
                dialog.DialogResult = true;
                return;
            }

            errorText.Visibility = Visibility.Visible;
            input.Focus();
            input.SelectAll();
        };

        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Width = 430
        };
        content.Children.Add(new TextBlock
        {
            Text = T("UiReferenceSearchCriteriaIntro"),
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        content.Children.Add(new TextBlock
        {
            Text = T("UiReferenceSearchMinimumParameterGroups"),
            Foreground = textBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = T("UiReferenceSearchMinimumParameterGroupsHelp"),
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        dialog.Content = content;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? selectedMinimum : null;
    }

    private void AnimalReferenceCompatibility_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyAnimalReferenceCompatibilityHighlight();
        viewModel.StatusMessage = "Compatibilité espèces évaluée sur la derniere mesure du contenant selectionne.";
    }

    private void AnimalReferenceApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyAnimalReferenceFilters();
        viewModel.StatusMessage = "Filtres espèces appliqués.";
    }

    private void AnimalReferenceResetFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ResetAnimalReferenceFilters();
        viewModel.StatusMessage = "Filtres espèces réinitialisés.";
    }

    private async void PlantReferenceEdit_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant modification.";
            return;
        }

        if (viewModel.SelectedPlantReference is null)
        {
            viewModel.StatusMessage = "Sélectionner une référence plante a modifier.";
            return;
        }

        var updated = ShowPlantReferenceEditDialog(viewModel.SelectedPlantReference);
        if (updated is null)
        {
            return;
        }

        try
        {
            await repository.UpdatePlantReferenceAsync(updated);
            await ReloadPlantReferencesAsync();
            viewModel.StatusMessage = "Référence plante modifiée.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Modification référence plante impossible: {ex.Message}";
            AppLogger.Error("Update plant reference failed.", ex);
        }
    }

    private PlantReference? ShowPlantReferenceEditDialog(PlantReferenceItem source)
    {
        var textBrush = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var secondaryBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.DimGray;
        var backgroundBrush = TryFindResource("CardBackgroundBrush") as Brush ?? Brushes.White;
        var inputBrush = TryFindResource("InputBackgroundBrush") as Brush ?? Brushes.White;
        var borderBrush = TryFindResource("CardBorderBrush") as Brush ?? Brushes.LightGray;

        var environmentInput = CreateEditComboBox(inputBrush, textBrush, borderBrush);
        environmentInput.Items.Add("Eau douce");
        environmentInput.Items.Add("Eau de mer");
        environmentInput.SelectedIndex = source.Environment == PlantReferenceEnvironment.Marine ? 1 : 0;

        var commonNameInput = CreateEditTextBox(source.CommonName);
        var commonNameFrInput = CreateEditTextBox(source.CommonNameFr);
        var commonNameEnInput = CreateEditTextBox(source.CommonNameEn);
        var commonNameDeInput = CreateEditTextBox(source.CommonNameDe);
        var scientificNameInput = CreateEditTextBox(source.ScientificName);
        var phMinInput = CreateEditTextBox(FormatNullable(source.PhMin));
        var phMaxInput = CreateEditTextBox(FormatNullable(source.PhMax));
        var ghMinInput = CreateEditTextBox(FormatNullable(source.GhMin));
        var ghMaxInput = CreateEditTextBox(FormatNullable(source.GhMax));
        var khMinInput = CreateEditTextBox(FormatNullable(source.KhMin));
        var khMaxInput = CreateEditTextBox(FormatNullable(source.KhMax));
        var temperatureMinInput = CreateEditTextBox(FormatNullable(source.TemperatureMin));
        var temperatureMaxInput = CreateEditTextBox(FormatNullable(source.TemperatureMax));
        var ammoniaMinInput = CreateEditTextBox(FormatNullable(source.AmmoniaMin));
        var ammoniaMaxInput = CreateEditTextBox(FormatNullable(source.AmmoniaMax));
        var nitritesMinInput = CreateEditTextBox(FormatNullable(source.NitritesMin));
        var nitritesMaxInput = CreateEditTextBox(FormatNullable(source.NitritesMax));
        var nitratesMinInput = CreateEditTextBox(FormatNullable(source.NitratesMin));
        var nitratesMaxInput = CreateEditTextBox(FormatNullable(source.NitratesMax));
        var volumeInput = CreateEditTextBox(source.VolumeMinLiters?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        var lightInput = CreateEditTextBox(source.LightNeed);
        var co2Input = CreateEditTextBox(source.Co2Need);
        var fertilizationInput = CreateEditTextBox(source.FertilizationNeed);
        var growthInput = CreateEditTextBox(source.GrowthSpeed);
        var placementInput = CreateEditTextBox(source.RecommendedPlacement);
        var behaviorInput = CreateEditTextBox(source.Behavior, 70, acceptsReturn: true);
        var compatibilityInput = CreateEditTextBox(source.Compatibility, 70, acceptsReturn: true);
        var sourceUrlInput = CreateEditTextBox(source.SourceUrl);

        var errorText = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var form = new Grid { Margin = new Thickness(18) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddEditRow(form, "Environnement", environmentInput, textBrush);
        AddEditRow(form, "Nom courant", commonNameInput, textBrush);
        AddEditRow(form, "Nom FR", commonNameFrInput, textBrush);
        AddEditRow(form, "Nom EN", commonNameEnInput, textBrush);
        AddEditRow(form, "Nom DE", commonNameDeInput, textBrush);
        AddEditRow(form, "Nom scientifique", scientificNameInput, textBrush);
        AddEditRow(form, "pH min", phMinInput, textBrush);
        AddEditRow(form, "pH max", phMaxInput, textBrush);
        AddEditRow(form, "GH min", ghMinInput, textBrush);
        AddEditRow(form, "GH max", ghMaxInput, textBrush);
        AddEditRow(form, "KH min", khMinInput, textBrush);
        AddEditRow(form, "KH max", khMaxInput, textBrush);
        AddEditRow(form, "Température min", temperatureMinInput, textBrush);
        AddEditRow(form, "Température max", temperatureMaxInput, textBrush);
        AddEditRow(form, "Amoniac min", ammoniaMinInput, textBrush);
        AddEditRow(form, "Amoniac max", ammoniaMaxInput, textBrush);
        AddEditRow(form, "Nitrites min", nitritesMinInput, textBrush);
        AddEditRow(form, "Nitrites max", nitritesMaxInput, textBrush);
        AddEditRow(form, "Nitrates min", nitratesMinInput, textBrush);
        AddEditRow(form, "Nitrates max", nitratesMaxInput, textBrush);
        AddEditRow(form, "Volume min (L)", volumeInput, textBrush);
        AddEditRow(form, "Lumière", lightInput, textBrush);
        AddEditRow(form, "CO2", co2Input, textBrush);
        AddEditRow(form, "Fertilisation", fertilizationInput, textBrush);
        AddEditRow(form, "Croissance", growthInput, textBrush);
        AddEditRow(form, "Emplacement", placementInput, textBrush);
        AddEditRow(form, "Comportement", behaviorInput, textBrush);
        AddEditRow(form, "Compatibilités", compatibilityInput, textBrush);
        AddEditRow(form, "URL source", sourceUrlInput, textBrush);

        var okButton = new Button { Content = "Enregistrer", Width = 110, Height = 32, IsDefault = true };
        var cancelButton = new Button { Content = "Annuler", Width = 90, Height = 32, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Corriger la référence plante sélectionnée. Les champs numériques vides resteront inconnus en base.",
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 0)
        });
        content.Children.Add(form);
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Modifier une référence plante",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 700,
            Height = 780,
            MinWidth = 640,
            MinHeight = 560,
            Background = backgroundBrush,
            Foreground = textBrush,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            }
        };

        PlantReference? result = null;
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(scientificNameInput.Text))
            {
                ShowEditError(errorText, "Le nom scientifique est obligatoire.");
                scientificNameInput.Focus();
                return;
            }

            if (!TryReadDecimal(phMinInput, "pH min", errorText, out var phMin)
                || !TryReadDecimal(phMaxInput, "pH max", errorText, out var phMax)
                || !TryReadDecimal(ghMinInput, "GH min", errorText, out var ghMin)
                || !TryReadDecimal(ghMaxInput, "GH max", errorText, out var ghMax)
                || !TryReadDecimal(khMinInput, "KH min", errorText, out var khMin)
                || !TryReadDecimal(khMaxInput, "KH max", errorText, out var khMax)
                || !TryReadDecimal(temperatureMinInput, "Température min", errorText, out var temperatureMin)
                || !TryReadDecimal(temperatureMaxInput, "Température max", errorText, out var temperatureMax)
                || !TryReadDecimal(ammoniaMinInput, "Amoniac min", errorText, out var ammoniaMin)
                || !TryReadDecimal(ammoniaMaxInput, "Amoniac max", errorText, out var ammoniaMax)
                || !TryReadDecimal(nitritesMinInput, "Nitrites min", errorText, out var nitritesMin)
                || !TryReadDecimal(nitritesMaxInput, "Nitrites max", errorText, out var nitritesMax)
                || !TryReadDecimal(nitratesMinInput, "Nitrates min", errorText, out var nitratesMin)
                || !TryReadDecimal(nitratesMaxInput, "Nitrates max", errorText, out var nitratesMax)
                || !TryReadInt(volumeInput, "Volume min", errorText, out var volumeMinLiters))
            {
                return;
            }

            var selectedEnvironment = environmentInput.SelectedIndex == 1
                ? PlantReferenceEnvironment.Marine
                : PlantReferenceEnvironment.FreshwaterTropical;

            result = new PlantReference
            {
                Id = source.Id,
                Environment = selectedEnvironment,
                CommonName = commonNameInput.Text.Trim(),
                CommonNameFr = commonNameFrInput.Text.Trim(),
                CommonNameEn = commonNameEnInput.Text.Trim(),
                CommonNameDe = commonNameDeInput.Text.Trim(),
                ScientificName = scientificNameInput.Text.Trim(),
                PhMin = phMin,
                PhMax = phMax,
                GhMin = ghMin,
                GhMax = ghMax,
                KhMin = khMin,
                KhMax = khMax,
                TemperatureMin = temperatureMin,
                TemperatureMax = temperatureMax,
                AmmoniaMin = ammoniaMin,
                AmmoniaMax = ammoniaMax,
                NitritesMin = nitritesMin,
                NitritesMax = nitritesMax,
                NitratesMin = nitratesMin,
                NitratesMax = nitratesMax,
                VolumeMinLiters = volumeMinLiters,
                LightNeed = lightInput.Text.Trim(),
                Co2Need = co2Input.Text.Trim(),
                FertilizationNeed = fertilizationInput.Text.Trim(),
                GrowthSpeed = growthInput.Text.Trim(),
                RecommendedPlacement = placementInput.Text.Trim(),
                Behavior = behaviorInput.Text.Trim(),
                Compatibility = compatibilityInput.Text.Trim(),
                SourceUrl = sourceUrlInput.Text.Trim()
            };
            dialog.DialogResult = true;
        };

        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? result : null;

        TextBox CreateEditTextBox(string value, double height = 32, bool acceptsReturn = false)
        {
            return new TextBox
            {
                Text = value,
                MinWidth = 360,
                Height = height,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = acceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = acceptsReturn,
                Background = inputBrush,
                Foreground = textBrush,
                BorderBrush = borderBrush
            };
        }
    }

    private async void AnimalReferenceEdit_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant modification.";
            return;
        }

        if (viewModel.SelectedAnimalReference is null)
        {
            viewModel.StatusMessage = "Sélectionner une référence animale a modifier.";
            return;
        }

        var updated = ShowAnimalReferenceEditDialog(viewModel.SelectedAnimalReference);
        if (updated is null)
        {
            return;
        }

        try
        {
            await repository.UpdateAnimalReferenceAsync(updated);
            await ReloadAnimalReferencesAsync();
            viewModel.StatusMessage = "Référence animale modifiée.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Modification référence impossible: {ex.Message}";
            AppLogger.Error("Update animal reference failed.", ex);
        }
    }

    private AnimalReference? ShowAnimalReferenceEditDialog(AnimalReferenceItem source)
    {
        var textBrush = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var secondaryBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.DimGray;
        var backgroundBrush = TryFindResource("CardBackgroundBrush") as Brush ?? Brushes.White;
        var inputBrush = TryFindResource("InputBackgroundBrush") as Brush ?? Brushes.White;
        var borderBrush = TryFindResource("CardBorderBrush") as Brush ?? Brushes.LightGray;

        var environmentInput = CreateEditComboBox(inputBrush, textBrush, borderBrush);
        environmentInput.Items.Add("Eau douce");
        environmentInput.Items.Add("Eau de mer");
        environmentInput.SelectedIndex = source.Environment == AnimalReferenceEnvironment.Marine ? 1 : 0;

        var groupInput = CreateEditComboBox(inputBrush, textBrush, borderBrush);
        var groupOptions = new[]
        {
            (Group: AnimalReferenceGroup.Fish, Label: "Poissons"),
            (Group: AnimalReferenceGroup.Shrimp, Label: "Crevettes"),
            (Group: AnimalReferenceGroup.Snail, Label: "Mollusques"),
            (Group: AnimalReferenceGroup.Other, Label: "Autres")
        };
        foreach (var option in groupOptions)
        {
            groupInput.Items.Add(option.Label);
        }

        groupInput.SelectedIndex = Math.Max(0, Array.FindIndex(groupOptions, option => option.Group == source.Group));

        var commonNameInput = CreateEditTextBox(source.CommonName);
        var commonNameFrInput = CreateEditTextBox(source.CommonNameFr);
        var commonNameEnInput = CreateEditTextBox(source.CommonNameEn);
        var commonNameDeInput = CreateEditTextBox(source.CommonNameDe);
        var scientificNameInput = CreateEditTextBox(source.ScientificName);
        var phMinInput = CreateEditTextBox(FormatNullable(source.PhMin));
        var phMaxInput = CreateEditTextBox(FormatNullable(source.PhMax));
        var ghMinInput = CreateEditTextBox(FormatNullable(source.GhMin));
        var ghMaxInput = CreateEditTextBox(FormatNullable(source.GhMax));
        var khMinInput = CreateEditTextBox(FormatNullable(source.KhMin));
        var khMaxInput = CreateEditTextBox(FormatNullable(source.KhMax));
        var temperatureMinInput = CreateEditTextBox(FormatNullable(source.TemperatureMin));
        var temperatureMaxInput = CreateEditTextBox(FormatNullable(source.TemperatureMax));
        var ammoniaMinInput = CreateEditTextBox(FormatNullable(source.AmmoniaMin));
        var ammoniaMaxInput = CreateEditTextBox(FormatNullable(source.AmmoniaMax));
        var nitritesMinInput = CreateEditTextBox(FormatNullable(source.NitritesMin));
        var nitritesMaxInput = CreateEditTextBox(FormatNullable(source.NitritesMax));
        var nitratesMinInput = CreateEditTextBox(FormatNullable(source.NitratesMin));
        var nitratesMaxInput = CreateEditTextBox(FormatNullable(source.NitratesMax));
        var volumeInput = CreateEditTextBox(source.VolumeMinLiters?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        var behaviorInput = CreateEditTextBox(source.Behavior, 70, acceptsReturn: true);
        var compatibilityInput = CreateEditTextBox(source.Compatibility, 70, acceptsReturn: true);
        var sourceUrlInput = CreateEditTextBox(source.SourceUrl);

        var errorText = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var form = new Grid { Margin = new Thickness(18) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddEditRow(form, "Environnement", environmentInput, textBrush);
        AddEditRow(form, "Groupe", groupInput, textBrush);
        AddEditRow(form, "Nom courant", commonNameInput, textBrush);
        AddEditRow(form, "Nom FR", commonNameFrInput, textBrush);
        AddEditRow(form, "Nom EN", commonNameEnInput, textBrush);
        AddEditRow(form, "Nom DE", commonNameDeInput, textBrush);
        AddEditRow(form, "Nom scientifique", scientificNameInput, textBrush);
        AddEditRow(form, "pH min", phMinInput, textBrush);
        AddEditRow(form, "pH max", phMaxInput, textBrush);
        AddEditRow(form, "GH min", ghMinInput, textBrush);
        AddEditRow(form, "GH max", ghMaxInput, textBrush);
        AddEditRow(form, "KH min", khMinInput, textBrush);
        AddEditRow(form, "KH max", khMaxInput, textBrush);
        AddEditRow(form, "Température min", temperatureMinInput, textBrush);
        AddEditRow(form, "Température max", temperatureMaxInput, textBrush);
        AddEditRow(form, "Amoniac min", ammoniaMinInput, textBrush);
        AddEditRow(form, "Amoniac max", ammoniaMaxInput, textBrush);
        AddEditRow(form, "Nitrites min", nitritesMinInput, textBrush);
        AddEditRow(form, "Nitrites max", nitritesMaxInput, textBrush);
        AddEditRow(form, "Nitrates min", nitratesMinInput, textBrush);
        AddEditRow(form, "Nitrates max", nitratesMaxInput, textBrush);
        AddEditRow(form, "Volume min (L)", volumeInput, textBrush);
        AddEditRow(form, "Comportement", behaviorInput, textBrush);
        AddEditRow(form, "Compatibilités", compatibilityInput, textBrush);
        AddEditRow(form, "URL source", sourceUrlInput, textBrush);

        var okButton = new Button { Content = "Enregistrer", Width = 110, Height = 32, IsDefault = true };
        var cancelButton = new Button { Content = "Annuler", Width = 90, Height = 32, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Corriger la référence sélectionnée. Les champs numériques vides resteront inconnus en base.",
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 0)
        });
        content.Children.Add(form);
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Modifier une référence animale",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 680,
            Height = 760,
            MinWidth = 640,
            MinHeight = 560,
            Background = backgroundBrush,
            Foreground = textBrush,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            }
        };

        AnimalReference? result = null;
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(scientificNameInput.Text))
            {
                ShowEditError(errorText, "Le nom scientifique est obligatoire.");
                scientificNameInput.Focus();
                return;
            }

            if (!TryReadDecimal(phMinInput, "pH min", errorText, out var phMin)
                || !TryReadDecimal(phMaxInput, "pH max", errorText, out var phMax)
                || !TryReadDecimal(ghMinInput, "GH min", errorText, out var ghMin)
                || !TryReadDecimal(ghMaxInput, "GH max", errorText, out var ghMax)
                || !TryReadDecimal(khMinInput, "KH min", errorText, out var khMin)
                || !TryReadDecimal(khMaxInput, "KH max", errorText, out var khMax)
                || !TryReadDecimal(temperatureMinInput, "Température min", errorText, out var temperatureMin)
                || !TryReadDecimal(temperatureMaxInput, "Température max", errorText, out var temperatureMax)
                || !TryReadDecimal(ammoniaMinInput, "Amoniac min", errorText, out var ammoniaMin)
                || !TryReadDecimal(ammoniaMaxInput, "Amoniac max", errorText, out var ammoniaMax)
                || !TryReadDecimal(nitritesMinInput, "Nitrites min", errorText, out var nitritesMin)
                || !TryReadDecimal(nitritesMaxInput, "Nitrites max", errorText, out var nitritesMax)
                || !TryReadDecimal(nitratesMinInput, "Nitrates min", errorText, out var nitratesMin)
                || !TryReadDecimal(nitratesMaxInput, "Nitrates max", errorText, out var nitratesMax)
                || !TryReadInt(volumeInput, "Volume min", errorText, out var volumeMinLiters))
            {
                return;
            }

            var selectedEnvironment = environmentInput.SelectedIndex == 1
                ? AnimalReferenceEnvironment.Marine
                : AnimalReferenceEnvironment.FreshwaterTropical;
            var selectedGroup = groupInput.SelectedIndex >= 0 && groupInput.SelectedIndex < groupOptions.Length
                ? groupOptions[groupInput.SelectedIndex].Group
                : AnimalReferenceGroup.Fish;

            result = new AnimalReference
            {
                Id = source.Id,
                Environment = selectedEnvironment,
                Group = selectedGroup,
                CommonName = commonNameInput.Text.Trim(),
                CommonNameFr = commonNameFrInput.Text.Trim(),
                CommonNameEn = commonNameEnInput.Text.Trim(),
                CommonNameDe = commonNameDeInput.Text.Trim(),
                ScientificName = scientificNameInput.Text.Trim(),
                PhMin = phMin,
                PhMax = phMax,
                GhMin = ghMin,
                GhMax = ghMax,
                KhMin = khMin,
                KhMax = khMax,
                TemperatureMin = temperatureMin,
                TemperatureMax = temperatureMax,
                AmmoniaMin = ammoniaMin,
                AmmoniaMax = ammoniaMax,
                NitritesMin = nitritesMin,
                NitritesMax = nitritesMax,
                NitratesMin = nitratesMin,
                NitratesMax = nitratesMax,
                VolumeMinLiters = volumeMinLiters,
                Behavior = behaviorInput.Text.Trim(),
                Compatibility = compatibilityInput.Text.Trim(),
                SourceUrl = sourceUrlInput.Text.Trim()
            };
            dialog.DialogResult = true;
        };

        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? result : null;

        TextBox CreateEditTextBox(string value, double height = 32, bool acceptsReturn = false)
        {
            return new TextBox
            {
                Text = value,
                MinWidth = 360,
                Height = height,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = acceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = acceptsReturn,
                Background = inputBrush,
                Foreground = textBrush,
                BorderBrush = borderBrush
            };
        }
    }

    private static void AddEditRow(Grid grid, string label, FrameworkElement input, Brush textBrush)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 12, 4)
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        input.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private ComboBox CreateEditComboBox(Brush inputBrush, Brush textBrush, Brush borderBrush)
    {
        var highlightBrush = TryFindResource("TabHeaderSelectedBackgroundBrush") as Brush ?? inputBrush;
        var comboBox = new ComboBox
        {
            Width = 260,
            Height = 32,
            MinHeight = 34,
            Background = inputBrush,
            Foreground = textBrush,
            BorderBrush = borderBrush,
            Style = TryFindResource(typeof(ComboBox)) as Style
        };

        comboBox.Resources[SystemColors.WindowBrushKey] = inputBrush;
        comboBox.Resources[SystemColors.WindowTextBrushKey] = textBrush;
        comboBox.Resources[SystemColors.ControlBrushKey] = inputBrush;
        comboBox.Resources[SystemColors.ControlTextBrushKey] = textBrush;
        comboBox.Resources[SystemColors.GrayTextBrushKey] = textBrush;
        comboBox.Resources[SystemColors.HighlightBrushKey] = highlightBrush;
        comboBox.Resources[SystemColors.HighlightTextBrushKey] = textBrush;
        comboBox.Resources["InputBackgroundBrush"] = inputBrush;
        comboBox.Resources["CardBorderBrush"] = borderBrush;
        comboBox.Resources["TextPrimaryBrush"] = textBrush;
        comboBox.Resources["TabHeaderSelectedBackgroundBrush"] = highlightBrush;

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, inputBrush));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        itemStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));

        var highlightedTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlightedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, highlightBrush));
        highlightedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        itemStyle.Triggers.Add(highlightedTrigger);

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, highlightBrush));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        itemStyle.Triggers.Add(selectedTrigger);

        comboBox.ItemContainerStyle = itemStyle;
        return comboBox;
    }

    private static bool TryReadDecimal(TextBox input, string label, TextBlock errorText, out decimal? value)
    {
        value = null;
        var text = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var current)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("fr-FR"), out current)
            || decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out current))
        {
            value = current;
            return true;
        }

        ShowEditError(errorText, $"Valeur numerique invalide pour {label}.");
        input.Focus();
        input.SelectAll();
        return false;
    }

    private static bool TryReadInt(TextBox input, string label, TextBlock errorText, out int? value)
    {
        value = null;
        var text = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        ShowEditError(errorText, $"Valeur entière invalide pour {label}.");
        input.Focus();
        input.SelectAll();
        return false;
    }

    private static string FormatNullable(decimal? value)
    {
        return value?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static void ShowEditError(TextBlock errorText, string message)
    {
        errorText.Text = message;
        errorText.Visibility = Visibility.Visible;
    }

    private async void AnimalReferenceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant suppression.";
            return;
        }

        if (viewModel.SelectedAnimalReference is null)
        {
            viewModel.StatusMessage = "Sélectionner une référence animale à supprimer.";
            return;
        }

        var reference = viewModel.SelectedAnimalReference;
        var result = MessageBox.Show(
            $"Supprimer la référence \"{reference.ScientificName}\" ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteAnimalReferenceAsync(reference.Id);
            await ReloadAnimalReferencesAsync();
            viewModel.StatusMessage = "Référence animale supprimée.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Suppression référence impossible: {ex.Message}";
            AppLogger.Error("Delete animal reference failed.", ex);
        }
    }

    private async void AnimalReferenceReset_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant réinitialisation.";
            return;
        }

        var result = MessageBox.Show(
            "Réinitialiser le référentiel population ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var before = await repository.GetAnimalReferenceCountAsync();
            await repository.ResetAnimalReferencesAsync();
            var after = await repository.GetAnimalReferenceCountAsync();
            await ReloadAnimalReferencesAsync();
            viewModel.StatusMessage = $"Référentiel population réinitialisé (avant: {before}, après: {after}).";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Réinitialisation population impossible: {ex.Message}";
            AppLogger.Error("Reset animal references failed.", ex);
        }
    }

    private void AnimalReferencesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AnimalReferenceItem item || string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.SourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Ouverture du lien impossible: {ex.Message}";
            AppLogger.Error("Open animal reference URL failed.", ex);
        }
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        RefreshApplicationLog();
        viewModel.StatusMessage = "Log rafraîchi.";
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppLogger.LogFilePath;
            if (!File.Exists(path))
            {
                AppLogger.Info("Log file requested by user before first write.");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Ouverture du log impossible: {ex.Message}";
            AppLogger.Error("Open log file failed.", ex);
        }
    }

    private void RefreshApplicationLog()
    {
        viewModel.ApplicationLogText = AppLogger.ReadTail();
        AppLogTextBox?.ScrollToEnd();
    }

    private void ApplyLanguage(string languageCode)
    {
        if (!localizedTexts.TryGetValue(languageCode, out var texts))
        {
            texts = localizedTexts[LanguageFrench];
            languageCode = LanguageFrench;
        }

        currentLanguage = languageCode;
        var culture = GetCultureForLanguage(languageCode);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

        viewModel.SetLanguage(languageCode);
        foreach (var pair in texts)
        {
            Resources[pair.Key] = pair.Value;
        }

        viewModel.NotifyLanguageChanged();
    }

    private static CultureInfo GetCultureForLanguage(string languageCode)
    {
        return CultureInfo.GetCultureInfo(languageCode switch
        {
            LanguageEnglish => "en-US",
            LanguageGerman => "de-DE",
            _ => "fr-FR"
        });
    }

    private void ApplyTheme(string themeCode)
    {
        currentTheme = NormalizeThemeCode(themeCode);
        var isDark = string.Equals(themeCode, ThemeDark, StringComparison.OrdinalIgnoreCase);
        var accentColor = ResolveAccentColor(currentAccentColor, isDark);
        Resources["AppBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#101617" : "#F3F7F8"));
        Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#1A2426" : "#FFFFFF"));
        Resources["CardBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#355055" : "#C8D8DA"));
        Resources["HeaderBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#0A2C31" : "#0E3F46"));
        Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E3F0F1" : "#172326"));
        Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#9AB2B6" : "#53696E"));
        Resources["TextOnHeaderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#D7ECEE" : "#D8EFF0"));
        Resources["ButtonPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor));
        Resources["ButtonDangerBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#B14A2A" : "#9A3412"));
        Resources["InputBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#223033" : "#FFFFFF"));
        Resources["DatePickerTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#FFFFFF" : "#172326"));
        Resources["TabHeaderBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#233437" : "#E7EFF0"));
        Resources["TabHeaderSelectedBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#2D4448" : "#D5E4E6"));

        // Keep ComboBox selected text/dropdown readable across dark/light themes.
        Resources[SystemColors.WindowBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#223033" : "#FFFFFF"));
        Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E3F0F1" : "#172326"));
        Resources[SystemColors.ControlBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#223033" : "#FFFFFF"));
        Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E3F0F1" : "#172326"));
        Resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#111111" : "#2A2A2A"));
        Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor));
        Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#172326" : "#172326"));
    }

    private void ApplyFontSize(string fontSizeCode)
    {
        currentFontSize = NormalizeFontSizeCode(fontSizeCode);
        FontSize = currentFontSize switch
        {
            FontSizeSmall => 11,
            FontSizeLarge => 14,
            _ => 12
        };
    }

    private void ApplyDensity(string densityCode)
    {
        currentDensity = NormalizeDensityCode(densityCode);
        var isCompact = string.Equals(currentDensity, DensityCompact, StringComparison.Ordinal);
        Resources["TextBoxMinHeight"] = isCompact ? 28d : 32d;
        Resources["ControlMinHeight"] = isCompact ? 30d : 36d;
        Resources["DataGridRowHeight"] = isCompact ? 26d : 32d;
        Resources["DataGridColumnHeaderHeight"] = isCompact ? 32d : 38d;
        Resources["ButtonPadding"] = isCompact ? new Thickness(10, 4, 10, 4) : new Thickness(16, 8, 16, 8);
    }

    private static string ResolveAccentColor(string accentColorCode, bool isDark)
    {
        return NormalizeAccentColorCode(accentColorCode) switch
        {
            AccentBlue => isDark ? "#3B82F6" : "#2563EB",
            AccentGreen => isDark ? "#22C55E" : "#15803D",
            AccentPurple => isDark ? "#A78BFA" : "#7C3AED",
            _ => isDark ? "#1A7F84" : "#156B6F"
        };
    }

    private static string NormalizeLanguageCode(string? code)
    {
        return code switch
        {
            LanguageEnglish => LanguageEnglish,
            LanguageGerman => LanguageGerman,
            _ => LanguageFrench
        };
    }

    private static string NormalizeThemeCode(string? code)
    {
        return string.Equals(code, ThemeDark, StringComparison.OrdinalIgnoreCase)
            ? ThemeDark
            : ThemeLight;
    }

    private static string NormalizeFontSizeCode(string? code)
    {
        return code?.Trim().ToLowerInvariant() switch
        {
            FontSizeSmall => FontSizeSmall,
            FontSizeLarge => FontSizeLarge,
            _ => FontSizeNormal
        };
    }

    private static string NormalizeDensityCode(string? code)
    {
        return string.Equals(code, DensityCompact, StringComparison.OrdinalIgnoreCase)
            ? DensityCompact
            : DensityComfortable;
    }

    private static string NormalizeAccentColorCode(string? code)
    {
        return code?.Trim().ToLowerInvariant() switch
        {
            AccentBlue => AccentBlue,
            AccentGreen => AccentGreen,
            AccentPurple => AccentPurple,
            _ => AccentTeal
        };
    }

    private static void SelectComboByTag(ComboBox comboBox, string tagValue)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string value && string.Equals(value, tagValue, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private string T(string key)
    {
        return localizedTexts.TryGetValue(currentLanguage, out var texts) && texts.TryGetValue(key, out var value)
            ? value
            : key;
    }

    private static Dictionary<string, Dictionary<string, string>> CreateLocalizedTexts()
    {
        var fr = new Dictionary<string, string>
        {
            ["UiAppSubtitle"] = "Gestion des aquariums, bassins, paramètres d'eau, plantes et population",
            ["UiSectionAquariums"] = "Contenants",
            ["UiButtonNewAquarium"] = "Nouveau contenant",
            ["UiButtonDeleteAquarium"] = "Supprimer contenant",
            ["UiTabSheet"] = "Fiche",
            ["UiTabParameters"] = "Paramètres",
            ["UiTabHealth"] = "Santé",
            ["UiTabPlants"] = "Plantes",
            ["UiTabPlantReference"] = "Référentiel plantes",
            ["UiTabPopulation"] = "Population",
            ["UiTabAnimalReference"] = "Référentiel population",
            ["UiTabInterventions"] = "Interventions",
            ["UiTabSettings"] = "Paramétrages",
            ["UiLabelName"] = "Nom",
            ["UiLabelVolume"] = "Volume (L)",
            ["UiLabelContainerType"] = "Type de contenant",
            ["UiContainerTypeAquarium"] = "Aquarium",
            ["UiContainerTypeFishPond"] = "Bassin à poissons",
            ["UiLabelWaterType"] = "Type d'eau",
            ["UiWaterTypeFreshwaterTropical"] = "Eau douce",
            ["UiWaterTypeFreshwaterPond"] = "Eau douce de bassin",
            ["UiWaterTypeMarine"] = "Eau de mer",
            ["UiPlantReferenceLabelUnknown"] = "Référentiel plantes - type inconnu",
            ["UiPlantReferenceLabelFreshwater"] = "Référentiel plantes - Eau douce",
            ["UiPlantReferenceLabelPond"] = "Référentiel plantes - Bassin à poissons",
            ["UiPlantReferenceLabelMarine"] = "Référentiel plantes - Eau de mer",
            ["UiAnimalReferenceLabelUnknown"] = "Référentiel population - type inconnu",
            ["UiAnimalReferenceLabelFreshwater"] = "Référentiel population - Eau douce",
            ["UiAnimalReferenceLabelPond"] = "Référentiel population - Bassin à poissons",
            ["UiAnimalReferenceLabelMarine"] = "Référentiel population - Eau de mer",
            ["UiLabelStartedOn"] = "Mise en eau",
            ["UiLabelNotes"] = "Notes",
            ["UiLabelAmmonia"] = "Amoniac mg/L",
            ["UiLabelNitrites"] = "Nitrites mg/L",
            ["UiLabelNitrates"] = "Nitrates mg/L",
            ["UiLabelPh"] = "pH",
            ["UiLabelGh"] = "GH",
            ["UiLabelKh"] = "KH",
            ["UiLabelTemperature"] = "Température C",
            ["UiWaterParameterAmmonia"] = "Amoniac",
            ["UiWaterParameterNitrites"] = "Nitrites",
            ["UiWaterParameterNitrates"] = "Nitrates",
            ["UiWaterParameterPh"] = "pH",
            ["UiWaterParameterGh"] = "GH",
            ["UiWaterParameterKh"] = "KH",
            ["UiWaterParameterTemperature"] = "Température",
            ["UiButtonAddMeasurement"] = "Ajouter la mesure",
            ["UiButtonDuplicateMeasurement"] = "Dupliquer la mesure",
            ["UiButtonDeleteMeasurement"] = "Supprimer la mesure",
            ["UiGridDate"] = "Date",
            ["UiAddedOn"] = "Date d'ajout",
            ["UiInventoryMovement"] = "Mouvement",
            ["UiMovementAddition"] = "Ajout",
            ["UiMovementRemoval"] = "Retrait",
            ["UiPlantQuantity"] = "Nombre",
            ["UiPlantCommonName"] = "Nom courant",
            ["UiPlantReferenceChoice"] = "Référentiel",
            ["UiPlantScientificName"] = "Nom scientifique",
            ["UiPlantRefNoData"] = "Aucune plante de référence pour ce type de contenant.",
            ["UiPlantRefEnvironment"] = "Type",
            ["UiAnimalRefGroup"] = "Groupe",
            ["UiAnimalGroupFish"] = "Poissons",
            ["UiAnimalGroupShrimp"] = "Crevettes",
            ["UiAnimalGroupSnail"] = "Mollusques",
            ["UiAnimalGroupOther"] = "Autres",
            ["UiPlantRefCommonName"] = "Nom courant",
            ["UiPlantRefScientificName"] = "Nom scientifique",
            ["UiPlantRefPhMin"] = "pH min",
            ["UiPlantRefPhMax"] = "pH max",
            ["UiPlantRefGhMin"] = "GH min",
            ["UiPlantRefGhMax"] = "GH max",
            ["UiPlantRefKhMin"] = "KH min",
            ["UiPlantRefKhMax"] = "KH max",
            ["UiPlantRefTempMin"] = "Temp min C",
            ["UiPlantRefTempMax"] = "Temp max C",
            ["UiPlantRefNh3Min"] = "Amoniac min",
            ["UiPlantRefNh3Max"] = "Amoniac max",
            ["UiPlantRefNo2Min"] = "Nitrites min",
            ["UiPlantRefNo2Max"] = "Nitrites max",
            ["UiPlantRefNo3Min"] = "Nitrates min",
            ["UiPlantRefNo3Max"] = "Nitrates max",
            ["UiPlantRefVolumeMin"] = "Volume min L",
            ["UiPlantRefLight"] = "Lumière",
            ["UiPlantRefCo2"] = "CO2",
            ["UiPlantRefFertilization"] = "Fertilisation",
            ["UiPlantRefGrowth"] = "Croissance",
            ["UiPlantRefPlacement"] = "Emplacement",
            ["UiPlantRefBehavior"] = "Comportement",
            ["UiPlantRefCompatibility"] = "Compatibilités",
            ["UiPlantRefSourceUrl"] = "Source URL",
            ["UiPlantRefSearchMore"] = "Recherches complémentaires",
            ["UiPlantRefCheckCompatibility"] = "Vérifier compatibilité",
            ["UiPlantRefEdit"] = "Modifier référence",
            ["UiPlantRefApplyFilters"] = "Appliquer filtres",
            ["UiPlantRefResetFilters"] = "Réinitialiser filtres",
            ["UiPlantRefDelete"] = "Supprimer référence",
            ["UiPlantRefResetCatalog"] = "Réinitialiser référentiel",
            ["UiPlantGrowth"] = "Croissance",
            ["UiGrowthSlow"] = "Lente",
            ["UiGrowthMedium"] = "Moyenne",
            ["UiGrowthFast"] = "Rapide",
            ["UiPlantLightNeed"] = "Lumière",
            ["UiPlantInventoryTotals"] = "Total plantes par espèce",
            ["UiLightLow"] = "Faible",
            ["UiLightMedium"] = "Moyenne",
            ["UiLightHigh"] = "Forte",
            ["UiButtonAddPlant"] = "Ajouter la plante",
            ["UiButtonDeletePlant"] = "Supprimer la plante",
            ["UiButtonAddPlantMovement"] = "Ajouter le mouvement",
            ["UiButtonDeletePlantMovement"] = "Supprimer la ligne",
            ["UiGridScientific"] = "Scientifique",
            ["UiGridGrowth"] = "Croissance",
            ["UiPopulationSpecies"] = "Espèce",
            ["UiAnimalReferenceChoice"] = "Référentiel faune",
            ["UiPopulationType"] = "Type",
            ["UiPopulationQuantity"] = "Quantité",
            ["UiPopulationFamily"] = "Famille",
            ["UiPopulationInventoryTotals"] = "Total population par espèce",
            ["UiPopulationTypeFish"] = "Poissons",
            ["UiPopulationTypeShrimp"] = "Crevettes",
            ["UiPopulationTypeSnail"] = "Mollusques",
            ["UiPopulationTypeOther"] = "Autres",
            ["UiButtonAddPopulation"] = "Ajouter la population",
            ["UiButtonDeletePopulation"] = "Supprimer la population",
            ["UiButtonAddPopulationMovement"] = "Ajouter le mouvement",
            ["UiButtonDeletePopulationMovement"] = "Supprimer la ligne",
            ["UiInterventionDate"] = "Date",
            ["UiInterventionTime"] = "Heure",
            ["UiInterventionType"] = "Type",
            ["UiInterventionWaterChange"] = "Changement d'eau",
            ["UiInterventionFertilization"] = "Fertilisation",
            ["UiInterventionFilterCleaning"] = "Nettoyage filtre",
            ["UiInterventionPopulationAdded"] = "Ajout population",
            ["UiInterventionPopulationRemoved"] = "Retrait population",
            ["UiInterventionMedicalTreatment"] = "Traitement médical",
            ["UiInterventionOther"] = "Autre",
            ["UiInterventionProductName"] = "Produit",
            ["UiInterventionProductQuantity"] = "Quantité produit",
            ["UiInterventionWaterVolume"] = "Eau remplacée (L)",
            ["UiInterventionWaterPercent"] = "Eau remplacée (%)",
            ["UiInterventionPopulationReason"] = "Raison population",
            ["UiInterventionPopulationCount"] = "Individus",
            ["UiButtonAddIntervention"] = "Ajouter l'intervention",
            ["UiButtonDeleteIntervention"] = "Supprimer l'intervention",
            ["UiDbActionsHelp"] = "Actions base de données et maintenance.",
            ["UiButtonConfigureMySql"] = "Configurer MySQL",
            ["UiButtonInitializeMySql"] = "Initialiser MySQL",
            ["UiButtonSave"] = "Sauvegarder",
            ["UiLabelLanguage"] = "Langue",
            ["UiLabelTheme"] = "Thème",
            ["UiSettingsDatabaseActions"] = "Base de données",
            ["UiSettingsLocalization"] = "Langue",
            ["UiSettingsAppearance"] = "Apparence",
            ["UiApplicationLog"] = "Journal applicatif",
            ["UiButtonRefreshLog"] = "Rafraîchir log",
            ["UiButtonOpenLog"] = "Ouvrir log",
            ["UiLabelFontSize"] = "Taille de police",
            ["UiFontSizeSmall"] = "Petite",
            ["UiFontSizeNormal"] = "Normale",
            ["UiFontSizeLarge"] = "Grande",
            ["UiLabelDensity"] = "Densité",
            ["UiDensityCompact"] = "Compacte",
            ["UiDensityComfortable"] = "Confortable",
            ["UiLabelAccentColor"] = "Couleur d'accentuation",
            ["UiAccentTeal"] = "Sarcelle",
            ["UiAccentBlue"] = "Bleu",
            ["UiAccentGreen"] = "Vert",
            ["UiAccentPurple"] = "Violet",
            ["UiHealthLastMeasure"] = "Dernière mesure",
            ["UiHealthGlobalStatus"] = "Statut global",
            ["UiHealthTrends"] = "Tendances",
            ["UiHealthCharts"] = "Graphiques d'évolution",
            ["UiHealthPeriod"] = "Période",
            ["UiHealthPeriod7"] = "7 jours",
            ["UiHealthPeriod30"] = "30 jours",
            ["UiHealthPeriod90"] = "90 jours",
            ["UiHealthPeriodAll"] = "Tout l'historique",
            ["UiHealthParameters"] = "Paramètres à afficher",
            ["UiHealthTargetRange"] = "Plage cible",
            ["UiHealthNoChartData"] = "Pas assez de mesures pour tracer un graphe.",
            ["UiHealthActions"] = "Actions conseillées",
            ["UiHealthNoData"] = "Aucune mesure disponible pour ce contenant.",
            ["UiHealthParameterColumn"] = "Paramètre",
            ["UiHealthValueColumn"] = "Valeur",
            ["UiHealthTrendColumn"] = "Tendance",
            ["UiHealthAlertColumn"] = "Alerte",
            ["HealthTrendNotAvailable"] = "N/A",
            ["HealthTrendUp"] = "Hausse",
            ["HealthTrendDown"] = "Baisse",
            ["HealthTrendStable"] = "Stable",
            ["UiLangFrench"] = "Français",
            ["UiLangEnglish"] = "Anglais",
            ["UiLangGerman"] = "Allemand",
            ["UiThemeLight"] = "Clair",
            ["UiThemeDark"] = "Sombre",
            ["UiFilterSearch"] = "Recherche",
            ["UiFilterPeriod"] = "Période",
            ["UiFilterMovement"] = "Mouvement",
            ["UiFilterPopulationType"] = "Famille",
            ["UiFilterInterventionType"] = "Type d'intervention",
            ["UiFilterAll"] = "Tout",
            ["UiMeasurementPeriod30"] = "30 jours",
            ["UiMeasurementPeriod90"] = "90 jours",
            ["UiClearFilters"] = "Effacer filtres",
            ["UiNoRowsMatchFilters"] = "Aucune ligne ne correspond aux filtres.",
            ["UiReferenceSearchCriteriaTitle"] = "Critères de recherche",
            ["UiReferenceSearchCriteriaIntro"] = "Choisis les critères utilisés pour filtrer les fiches candidates avant insertion dans le référentiel.",
            ["UiReferenceSearchMinimumParameterGroups"] = "Nombre minimal de groupes de paramètres",
            ["UiReferenceSearchMinimumParameterGroupsHelp"] = "Valeur entre 1 et 8. Plus le nombre est élevé, plus les espèces importées seront documentées.",
            ["UiReferenceSearchInvalidMinimum"] = "Saisis un nombre entier entre 1 et 8.",
            ["UiDialogOk"] = "OK",
            ["UiDialogCancel"] = "Annuler",
            ["StatusReady"] = "Prêt. MySQL est optionnel au démarrage pour garder l'application utilisable hors ligne.",
            ["StatusMySqlNotConfigured"] = "MySQL non configuré. Utilise Configurer MySQL pour enregistrer une connexion locale.",
            ["StatusMySqlConfiguredSecure"] = "MySQL configuré depuis la configuration locale sécurisée.",
            ["StatusMySqlConfiguredEnv"] = "MySQL configuré via ADAQUA_MYSQL_CONNECTION_STRING.",
            ["StatusReadFailed"] = "Lecture MySQL indisponible:",
            ["StatusMySqlConfigSaved"] = "Configuration MySQL enregistrée et active.",
            ["StatusConfigSavedReadFailed"] = "Configuration enregistrée, mais lecture MySQL impossible:",
            ["StatusConfigureBeforeInit"] = "Configure MySQL avant d'initialiser le schéma.",
            ["StatusSchemaInitialized"] = "Schéma MySQL initialisé.",
            ["StatusInitializationFailed"] = "Initialisation MySQL impossible:",
            ["StatusConnectedNoAquarium"] = "MySQL connecté. Aucun contenant en base pour le moment.",
            ["StatusConnectedAquariumCount"] = "MySQL connecté. {0} contenant(s) chargé(s).",
            ["StatusLocalModeConfigure"] = "Mode local: configure MySQL pour sauvegarder en base.",
            ["StatusSaveInvalidInput"] = "Sauvegarde impossible: corrige les erreurs de saisie avant d'enregistrer.",
            ["StatusAquariumSaved"] = "Contenant sauvegardé dans MySQL.",
            ["StatusSaveFailed"] = "Enregistrement MySQL impossible:",
            ["StatusNewAquariumSaved"] = "Nouveau contenant enregistré.",
            ["StatusAquariumDeleted"] = "Contenant supprimé.",
            ["StatusAquariumDeleteFailed"] = "Suppression du contenant impossible:",
            ["StatusSelectMeasurementDelete"] = "Sélectionne une mesure à supprimer.",
            ["StatusSelectMeasurementDuplicate"] = "Sélectionne une mesure à dupliquer.",
            ["StatusMeasurementSaved"] = "Mesure d'eau enregistrée.",
            ["StatusMeasurementDuplicated"] = "Mesure d'eau dupliquée et enregistrée.",
            ["StatusMeasurementDeleted"] = "Mesure d'eau supprimée.",
            ["StatusSelectPlantDelete"] = "Sélectionne une plante à supprimer.",
            ["StatusPlantSaved"] = "Plante enregistrée.",
            ["StatusPlantDeleted"] = "Plante supprimée.",
            ["StatusSelectPopulationDelete"] = "Sélectionne une population à supprimer.",
            ["StatusPopulationSaved"] = "Population enregistrée.",
            ["StatusPopulationDeleted"] = "Population supprimée.",
            ["StatusInterventionInvalidTime"] = "Heure d'intervention invalide. Utilise le format HH:mm.",
            ["StatusSelectInterventionDelete"] = "Sélectionne une intervention à supprimer.",
            ["StatusInterventionSaved"] = "Intervention enregistrée.",
            ["StatusInterventionDeleted"] = "Intervention supprimée.",
            ["StatusLanguageChanged"] = "Langue appliquée.",
            ["StatusThemeChanged"] = "Thème appliqué.",
            ["StatusAppearanceChanged"] = "Apparence appliquée.",
            ["HealthStatusNoData"] = "Aucune mesure",
            ["HealthStatusOk"] = "Stable",
            ["HealthStatusWarning"] = "Alerte modérée",
            ["HealthStatusCritical"] = "Alerte critique",
            ["HealthAlertNoData"] = "N/A",
            ["HealthAlertOk"] = "OK",
            ["HealthAlertWarning"] = "À surveiller",
            ["HealthAlertCritical"] = "Critique",
            ["HealthActionNoData"] = "Ajoute une mesure d'eau pour activer le suivi de santé.",
            ["HealthActionOk"] = "Paramètres dans les plages cibles. Continuer la routine actuelle.",
            ["HealthActionWarningNitrates"] = "Prévoir un changement d'eau partiel pour réduire les nitrates.",
            ["HealthActionWarningPh"] = "Vérifier le pH et ajuster progressivement si nécessaire.",
            ["HealthActionWarningHardness"] = "Vérifier GH/KH et adapter l'eau de remplacement.",
            ["HealthActionWarningGeneric"] = "Surveiller l'évolution sur les prochaines mesures.",
            ["HealthActionCriticalWaterChange"] = "Effectuer un changement d'eau rapide et vérifier filtration/aération.",
            ["HealthActionCriticalFeeding"] = "Réduire la nourriture temporairement pour limiter la charge azotée.",
            ["HealthActionCriticalTemperature"] = "Corriger la température (chauffage/refroidissement) sans variation brutale.",
            ["HealthActionCriticalGeneric"] = "Analyser l'eau et stabiliser les paramètres critiques en priorité.",
            ["ConfirmDeleteTitle"] = "Confirmation de suppression",
            ["ConfirmDeleteAquarium"] = "Supprimer le contenant \"{0}\" et toutes ses données associées ?",
            ["ConfirmDeleteMeasurement"] = "Supprimer la mesure du {0:g} ?",
            ["ConfirmDeletePlant"] = "Supprimer la plante \"{0}\" ?",
            ["ConfirmDeletePopulation"] = "Supprimer \"{0}\" de la population ?",
            ["ConfirmDeleteIntervention"] = "Supprimer l'intervention du {0:g} ?",
            ["DefaultWaterType"] = "Eau douce",
            ["DefaultMainAquarium"] = "Bac principal",
            ["DefaultMainAquariumNote"] = "Premier contenant ADAqua.",
            ["DefaultPlantLight"] = "Faible",
            ["DefaultNeonName"] = "Néon bleu"
        };

        var en = new Dictionary<string, string>(fr)
        {
            ["UiAppSubtitle"] = "Manage aquariums, ponds, water parameters, plants and population",
            ["UiSectionAquariums"] = "Containers",
            ["UiButtonNewAquarium"] = "New container",
            ["UiButtonDeleteAquarium"] = "Delete container",
            ["UiTabSheet"] = "Overview",
            ["UiTabParameters"] = "Parameters",
            ["UiTabHealth"] = "Health",
            ["UiTabPlants"] = "Plants",
            ["UiTabPlantReference"] = "Plant Reference",
            ["UiTabPopulation"] = "Population",
            ["UiTabAnimalReference"] = "Population Reference",
            ["UiTabInterventions"] = "Interventions",
            ["UiTabSettings"] = "Settings",
            ["UiLabelName"] = "Name",
            ["UiLabelVolume"] = "Volume (L)",
            ["UiLabelContainerType"] = "Container type",
            ["UiContainerTypeAquarium"] = "Aquarium",
            ["UiContainerTypeFishPond"] = "Fish pond",
            ["UiLabelWaterType"] = "Water type",
            ["UiWaterTypeFreshwaterTropical"] = "Freshwater",
            ["UiWaterTypeFreshwaterPond"] = "Freshwater pond",
            ["UiWaterTypeMarine"] = "Marine",
            ["UiLabelAmmonia"] = "Ammonia mg/L",
            ["UiLabelNitrites"] = "Nitrites mg/L",
            ["UiLabelNitrates"] = "Nitrates mg/L",
            ["UiLabelTemperature"] = "Temperature C",
            ["UiWaterParameterAmmonia"] = "Ammonia",
            ["UiWaterParameterNitrites"] = "Nitrites",
            ["UiWaterParameterNitrates"] = "Nitrates",
            ["UiWaterParameterPh"] = "pH",
            ["UiWaterParameterGh"] = "GH",
            ["UiWaterParameterKh"] = "KH",
            ["UiWaterParameterTemperature"] = "Temperature",
            ["UiPlantReferenceLabelUnknown"] = "Plant reference - unknown type",
            ["UiPlantReferenceLabelFreshwater"] = "Plant reference - freshwater",
            ["UiPlantReferenceLabelPond"] = "Plant reference - fish pond",
            ["UiPlantReferenceLabelMarine"] = "Plant reference - marine",
            ["UiAnimalReferenceLabelUnknown"] = "Population reference - unknown type",
            ["UiAnimalReferenceLabelFreshwater"] = "Population reference - freshwater",
            ["UiAnimalReferenceLabelPond"] = "Population reference - fish pond",
            ["UiAnimalReferenceLabelMarine"] = "Population reference - marine",
            ["UiLabelStartedOn"] = "Start date",
            ["UiAddedOn"] = "Added on",
            ["UiInventoryMovement"] = "Movement",
            ["UiMovementAddition"] = "Addition",
            ["UiMovementRemoval"] = "Removal",
            ["UiPlantQuantity"] = "Count",
            ["UiPlantCommonName"] = "Common name",
            ["UiPlantReferenceChoice"] = "Reference catalog",
            ["UiPlantScientificName"] = "Scientific name",
            ["UiPlantRefNoData"] = "No plant reference for this container type.",
            ["UiPlantRefEnvironment"] = "Environment",
            ["UiAnimalRefGroup"] = "Group",
            ["UiAnimalGroupFish"] = "Fish",
            ["UiAnimalGroupShrimp"] = "Shrimp",
            ["UiAnimalGroupSnail"] = "Molluscs",
            ["UiAnimalGroupOther"] = "Other",
            ["UiPlantRefCommonName"] = "Common name",
            ["UiPlantRefScientificName"] = "Scientific name",
            ["UiPlantRefPhMin"] = "pH min",
            ["UiPlantRefPhMax"] = "pH max",
            ["UiPlantRefGhMin"] = "GH min",
            ["UiPlantRefGhMax"] = "GH max",
            ["UiPlantRefKhMin"] = "KH min",
            ["UiPlantRefKhMax"] = "KH max",
            ["UiPlantRefTempMin"] = "Temp min C",
            ["UiPlantRefTempMax"] = "Temp max C",
            ["UiPlantRefNh3Min"] = "Ammonia min",
            ["UiPlantRefNh3Max"] = "Ammonia max",
            ["UiPlantRefNo2Min"] = "Nitrites min",
            ["UiPlantRefNo2Max"] = "Nitrites max",
            ["UiPlantRefNo3Min"] = "Nitrates min",
            ["UiPlantRefNo3Max"] = "Nitrates max",
            ["UiPlantRefVolumeMin"] = "Min volume L",
            ["UiPlantRefLight"] = "Light",
            ["UiPlantRefCo2"] = "CO2",
            ["UiPlantRefFertilization"] = "Fertilization",
            ["UiPlantRefGrowth"] = "Growth",
            ["UiPlantRefPlacement"] = "Placement",
            ["UiPlantRefBehavior"] = "Behavior",
            ["UiPlantRefCompatibility"] = "Compatibility",
            ["UiPlantRefSourceUrl"] = "Source URL",
            ["UiPlantRefSearchMore"] = "Additional searches",
            ["UiPlantRefCheckCompatibility"] = "Check compatibility",
            ["UiPlantRefEdit"] = "Edit reference",
            ["UiPlantRefApplyFilters"] = "Apply filters",
            ["UiPlantRefResetFilters"] = "Reset filters",
            ["UiPlantRefDelete"] = "Delete reference",
            ["UiPlantRefResetCatalog"] = "Reset catalog",
            ["UiPlantGrowth"] = "Growth",
            ["UiGrowthSlow"] = "Slow",
            ["UiGrowthMedium"] = "Medium",
            ["UiGrowthFast"] = "Fast",
            ["UiPlantLightNeed"] = "Light",
            ["UiPlantInventoryTotals"] = "Plant totals by species",
            ["UiLightLow"] = "Low",
            ["UiLightMedium"] = "Medium",
            ["UiLightHigh"] = "High",
            ["UiButtonAddMeasurement"] = "Add measurement",
            ["UiButtonDuplicateMeasurement"] = "Duplicate measurement",
            ["UiButtonDeleteMeasurement"] = "Delete measurement",
            ["UiButtonAddPlant"] = "Add plant",
            ["UiButtonDeletePlant"] = "Delete plant",
            ["UiButtonAddPlantMovement"] = "Add movement",
            ["UiButtonDeletePlantMovement"] = "Delete row",
            ["UiGridScientific"] = "Scientific",
            ["UiGridGrowth"] = "Growth",
            ["UiPopulationSpecies"] = "Species",
            ["UiAnimalReferenceChoice"] = "Animal reference",
            ["UiPopulationType"] = "Type",
            ["UiPopulationQuantity"] = "Quantity",
            ["UiPopulationFamily"] = "Family",
            ["UiPopulationInventoryTotals"] = "Population totals by species",
            ["UiPopulationTypeFish"] = "Fish",
            ["UiPopulationTypeShrimp"] = "Shrimp",
            ["UiPopulationTypeSnail"] = "Molluscs",
            ["UiPopulationTypeOther"] = "Other",
            ["UiButtonAddPopulation"] = "Add population",
            ["UiButtonDeletePopulation"] = "Delete population",
            ["UiButtonAddPopulationMovement"] = "Add movement",
            ["UiButtonDeletePopulationMovement"] = "Delete row",
            ["UiInterventionDate"] = "Date",
            ["UiInterventionTime"] = "Time",
            ["UiInterventionType"] = "Type",
            ["UiInterventionWaterChange"] = "Water change",
            ["UiInterventionFertilization"] = "Fertilization",
            ["UiInterventionFilterCleaning"] = "Filter cleaning",
            ["UiInterventionPopulationAdded"] = "Population added",
            ["UiInterventionPopulationRemoved"] = "Population removed",
            ["UiInterventionMedicalTreatment"] = "Medical treatment",
            ["UiInterventionOther"] = "Other",
            ["UiInterventionProductName"] = "Product",
            ["UiInterventionProductQuantity"] = "Product quantity",
            ["UiInterventionWaterVolume"] = "Water replaced (L)",
            ["UiInterventionWaterPercent"] = "Water replaced (%)",
            ["UiInterventionPopulationReason"] = "Population reason",
            ["UiInterventionPopulationCount"] = "Individuals",
            ["UiButtonAddIntervention"] = "Add intervention",
            ["UiButtonDeleteIntervention"] = "Delete intervention",
            ["UiDbActionsHelp"] = "Database and maintenance actions.",
            ["UiButtonConfigureMySql"] = "Configure MySQL",
            ["UiButtonInitializeMySql"] = "Initialize MySQL",
            ["UiButtonSave"] = "Save",
            ["UiLabelLanguage"] = "Language",
            ["UiLabelTheme"] = "Theme",
            ["UiSettingsDatabaseActions"] = "Database",
            ["UiSettingsLocalization"] = "Language",
            ["UiSettingsAppearance"] = "Appearance",
            ["UiApplicationLog"] = "Application log",
            ["UiButtonRefreshLog"] = "Refresh log",
            ["UiButtonOpenLog"] = "Open log",
            ["UiLabelFontSize"] = "Font size",
            ["UiFontSizeSmall"] = "Small",
            ["UiFontSizeNormal"] = "Normal",
            ["UiFontSizeLarge"] = "Large",
            ["UiLabelDensity"] = "Density",
            ["UiDensityCompact"] = "Compact",
            ["UiDensityComfortable"] = "Comfortable",
            ["UiLabelAccentColor"] = "Accent color",
            ["UiAccentTeal"] = "Teal",
            ["UiAccentBlue"] = "Blue",
            ["UiAccentGreen"] = "Green",
            ["UiAccentPurple"] = "Purple",
            ["UiHealthLastMeasure"] = "Last measurement",
            ["UiHealthGlobalStatus"] = "Global status",
            ["UiHealthTrends"] = "Trends",
            ["UiHealthCharts"] = "Trend charts",
            ["UiHealthPeriod"] = "Period",
            ["UiHealthPeriod7"] = "7 days",
            ["UiHealthPeriod30"] = "30 days",
            ["UiHealthPeriod90"] = "90 days",
            ["UiHealthPeriodAll"] = "Full history",
            ["UiHealthParameters"] = "Parameters to display",
            ["UiHealthTargetRange"] = "Target range",
            ["UiHealthNoChartData"] = "Not enough measurements to draw a chart.",
            ["UiHealthActions"] = "Recommended actions",
            ["UiHealthNoData"] = "No measurement available for this container.",
            ["UiHealthParameterColumn"] = "Parameter",
            ["UiHealthValueColumn"] = "Value",
            ["UiHealthTrendColumn"] = "Trend",
            ["UiHealthAlertColumn"] = "Alert",
            ["HealthTrendNotAvailable"] = "N/A",
            ["HealthTrendUp"] = "Rising",
            ["HealthTrendDown"] = "Falling",
            ["HealthTrendStable"] = "Stable",
            ["UiLangFrench"] = "French",
            ["UiLangEnglish"] = "English",
            ["UiLangGerman"] = "German",
            ["UiThemeLight"] = "Light",
            ["UiThemeDark"] = "Dark",
            ["UiFilterSearch"] = "Search",
            ["UiFilterPeriod"] = "Period",
            ["UiFilterMovement"] = "Movement",
            ["UiFilterPopulationType"] = "Family",
            ["UiFilterInterventionType"] = "Intervention type",
            ["UiFilterAll"] = "All",
            ["UiMeasurementPeriod30"] = "30 days",
            ["UiMeasurementPeriod90"] = "90 days",
            ["UiClearFilters"] = "Clear filters",
            ["UiNoRowsMatchFilters"] = "No row matches the filters.",
            ["UiReferenceSearchCriteriaTitle"] = "Search criteria",
            ["UiReferenceSearchCriteriaIntro"] = "Choose the criteria used to filter candidate records before inserting them into the reference catalog.",
            ["UiReferenceSearchMinimumParameterGroups"] = "Minimum number of parameter groups",
            ["UiReferenceSearchMinimumParameterGroupsHelp"] = "Value from 1 to 8. The higher the number, the better documented imported species will be.",
            ["UiReferenceSearchInvalidMinimum"] = "Enter a whole number from 1 to 8.",
            ["UiDialogOk"] = "OK",
            ["UiDialogCancel"] = "Cancel",
            ["StatusReady"] = "Ready. MySQL is optional at startup to keep the app usable offline.",
            ["StatusMySqlNotConfigured"] = "MySQL not configured. Use Configure MySQL to save a local connection.",
            ["StatusMySqlConfiguredSecure"] = "MySQL configured from secure local settings.",
            ["StatusMySqlConfiguredEnv"] = "MySQL configured via ADAQUA_MYSQL_CONNECTION_STRING.",
            ["StatusReadFailed"] = "MySQL read unavailable:",
            ["StatusMySqlConfigSaved"] = "MySQL configuration saved and active.",
            ["StatusConfigSavedReadFailed"] = "Configuration saved, but MySQL read failed:",
            ["StatusConfigureBeforeInit"] = "Configure MySQL before initializing schema.",
            ["StatusSchemaInitialized"] = "MySQL schema initialized.",
            ["StatusInitializationFailed"] = "MySQL initialization failed:",
            ["StatusConnectedNoAquarium"] = "MySQL connected. No containers in database yet.",
            ["StatusConnectedAquariumCount"] = "MySQL connected. {0} container(s) loaded.",
            ["StatusLocalModeConfigure"] = "Local mode: configure MySQL to save to database.",
            ["StatusSaveInvalidInput"] = "Save failed: fix input errors before saving.",
            ["StatusAquariumSaved"] = "Container saved to MySQL.",
            ["StatusSaveFailed"] = "MySQL save failed:",
            ["StatusNewAquariumSaved"] = "New container saved.",
            ["StatusAquariumDeleted"] = "Container deleted.",
            ["StatusAquariumDeleteFailed"] = "Container deletion failed:",
            ["StatusSelectMeasurementDelete"] = "Select a measurement to delete.",
            ["StatusSelectMeasurementDuplicate"] = "Select a measurement to duplicate.",
            ["StatusMeasurementSaved"] = "Measurement saved.",
            ["StatusMeasurementDuplicated"] = "Measurement duplicated and saved.",
            ["StatusMeasurementDeleted"] = "Measurement deleted.",
            ["StatusSelectPlantDelete"] = "Select a plant to delete.",
            ["StatusPlantSaved"] = "Plant saved.",
            ["StatusPlantDeleted"] = "Plant deleted.",
            ["StatusSelectPopulationDelete"] = "Select a population entry to delete.",
            ["StatusPopulationSaved"] = "Population saved.",
            ["StatusPopulationDeleted"] = "Population deleted.",
            ["StatusInterventionInvalidTime"] = "Invalid intervention time. Use HH:mm format.",
            ["StatusSelectInterventionDelete"] = "Select an intervention to delete.",
            ["StatusInterventionSaved"] = "Intervention saved.",
            ["StatusInterventionDeleted"] = "Intervention deleted.",
            ["StatusLanguageChanged"] = "Language applied.",
            ["StatusThemeChanged"] = "Theme applied.",
            ["StatusAppearanceChanged"] = "Appearance applied.",
            ["HealthStatusNoData"] = "No measurements",
            ["HealthStatusOk"] = "Stable",
            ["HealthStatusWarning"] = "Moderate alert",
            ["HealthStatusCritical"] = "Critical alert",
            ["HealthAlertNoData"] = "N/A",
            ["HealthAlertOk"] = "OK",
            ["HealthAlertWarning"] = "Watch",
            ["HealthAlertCritical"] = "Critical",
            ["HealthActionNoData"] = "Add a water measurement to enable health tracking.",
            ["HealthActionOk"] = "Parameters are within target ranges. Keep current routine.",
            ["HealthActionWarningNitrates"] = "Plan a partial water change to lower nitrates.",
            ["HealthActionWarningPh"] = "Check pH and adjust gradually if needed.",
            ["HealthActionWarningHardness"] = "Check GH/KH and adapt replacement water.",
            ["HealthActionWarningGeneric"] = "Monitor changes in the next measurements.",
            ["HealthActionCriticalWaterChange"] = "Perform a quick water change and check filtration/aeration.",
            ["HealthActionCriticalFeeding"] = "Reduce feeding temporarily to limit nitrogen load.",
            ["HealthActionCriticalTemperature"] = "Correct temperature (heating/cooling) without abrupt changes.",
            ["HealthActionCriticalGeneric"] = "Analyze water and stabilize critical parameters first.",
            ["ConfirmDeleteTitle"] = "Delete confirmation",
            ["ConfirmDeleteAquarium"] = "Delete container \"{0}\" and all related data?",
            ["ConfirmDeleteMeasurement"] = "Delete measurement from {0:g}?",
            ["ConfirmDeletePlant"] = "Delete plant \"{0}\"?",
            ["ConfirmDeletePopulation"] = "Delete \"{0}\" from population?",
            ["ConfirmDeleteIntervention"] = "Delete intervention from {0:g}?",
            ["DefaultWaterType"] = "Freshwater",
            ["DefaultMainAquarium"] = "Main tank",
            ["DefaultMainAquariumNote"] = "First ADAqua container.",
            ["DefaultPlantLight"] = "Low",
            ["DefaultNeonName"] = "Neon tetra"
        };

        var de = new Dictionary<string, string>(fr)
        {
            ["UiAppSubtitle"] = "Verwaltung von Aquarien, Wasserwerten, Pflanzen und Besatz",
            ["UiAppSubtitle"] = "Aquarien, Teiche, Wasserparameter, Pflanzen und Besatz verwalten",
            ["UiSectionAquariums"] = "Behaelter",
            ["UiButtonNewAquarium"] = "Neuer Behaelter",
            ["UiButtonDeleteAquarium"] = "Behaelter loeschen",
            ["UiTabSheet"] = "Uebersicht",
            ["UiTabParameters"] = "Parameter",
            ["UiTabHealth"] = "Gesundheit",
            ["UiTabPlants"] = "Pflanzen",
            ["UiTabPlantReference"] = "Pflanzenkatalog",
            ["UiTabPopulation"] = "Besatz",
            ["UiTabAnimalReference"] = "Besatzkatalog",
            ["UiTabInterventions"] = "Eingriffe",
            ["UiTabSettings"] = "Einstellungen",
            ["UiLabelName"] = "Name",
            ["UiLabelVolume"] = "Volumen (L)",
            ["UiLabelContainerType"] = "Behaeltertyp",
            ["UiContainerTypeAquarium"] = "Aquarium",
            ["UiContainerTypeFishPond"] = "Fischteich",
            ["UiLabelWaterType"] = "Wassertyp",
            ["UiWaterTypeFreshwaterTropical"] = "Suesswasser",
            ["UiWaterTypeFreshwaterPond"] = "Teich-Suesswasser",
            ["UiWaterTypeMarine"] = "Meerwasser",
            ["UiLabelAmmonia"] = "Ammoniak mg/L",
            ["UiLabelNitrites"] = "Nitrit mg/L",
            ["UiLabelNitrates"] = "Nitrat mg/L",
            ["UiLabelTemperature"] = "Temperatur C",
            ["UiWaterParameterAmmonia"] = "Ammoniak",
            ["UiWaterParameterNitrites"] = "Nitrit",
            ["UiWaterParameterNitrates"] = "Nitrat",
            ["UiWaterParameterPh"] = "pH",
            ["UiWaterParameterGh"] = "GH",
            ["UiWaterParameterKh"] = "KH",
            ["UiWaterParameterTemperature"] = "Temperatur",
            ["UiPlantReferenceLabelUnknown"] = "Pflanzenkatalog - unbekannter Typ",
            ["UiPlantReferenceLabelFreshwater"] = "Pflanzenkatalog - Suesswasser",
            ["UiPlantReferenceLabelPond"] = "Pflanzenkatalog - Fischteich",
            ["UiPlantReferenceLabelMarine"] = "Pflanzenkatalog - Meerwasser",
            ["UiAnimalReferenceLabelUnknown"] = "Besatzkatalog - unbekannter Typ",
            ["UiAnimalReferenceLabelFreshwater"] = "Besatzkatalog - Suesswasser",
            ["UiAnimalReferenceLabelPond"] = "Besatzkatalog - Fischteich",
            ["UiAnimalReferenceLabelMarine"] = "Besatzkatalog - Meerwasser",
            ["UiLabelStartedOn"] = "Startdatum",
            ["UiAddedOn"] = "Hinzugefuegt am",
            ["UiInventoryMovement"] = "Bewegung",
            ["UiMovementAddition"] = "Zugang",
            ["UiMovementRemoval"] = "Abgang",
            ["UiPlantQuantity"] = "Anzahl",
            ["UiPlantCommonName"] = "Trivialname",
            ["UiPlantReferenceChoice"] = "Pflanzenkatalog",
            ["UiPlantScientificName"] = "Wissenschaftlicher Name",
            ["UiPlantRefNoData"] = "Keine Pflanzenreferenz fuer diesen Behaeltertyp.",
            ["UiPlantRefEnvironment"] = "Umgebung",
            ["UiAnimalRefGroup"] = "Gruppe",
            ["UiAnimalGroupFish"] = "Fische",
            ["UiAnimalGroupShrimp"] = "Garnelen",
            ["UiAnimalGroupSnail"] = "Mollusken",
            ["UiAnimalGroupOther"] = "Andere",
            ["UiPlantRefCommonName"] = "Trivialname",
            ["UiPlantRefScientificName"] = "Wissenschaftlicher Name",
            ["UiPlantRefPhMin"] = "pH min",
            ["UiPlantRefPhMax"] = "pH max",
            ["UiPlantRefGhMin"] = "GH min",
            ["UiPlantRefGhMax"] = "GH max",
            ["UiPlantRefKhMin"] = "KH min",
            ["UiPlantRefKhMax"] = "KH max",
            ["UiPlantRefTempMin"] = "Temp. min C",
            ["UiPlantRefTempMax"] = "Temp. max C",
            ["UiPlantRefNh3Min"] = "Ammoniak min",
            ["UiPlantRefNh3Max"] = "Ammoniak max",
            ["UiPlantRefNo2Min"] = "Nitrit min",
            ["UiPlantRefNo2Max"] = "Nitrit max",
            ["UiPlantRefNo3Min"] = "Nitrat min",
            ["UiPlantRefNo3Max"] = "Nitrat max",
            ["UiPlantRefVolumeMin"] = "Mindestvolumen L",
            ["UiPlantRefLight"] = "Licht",
            ["UiPlantRefCo2"] = "CO2",
            ["UiPlantRefFertilization"] = "Duengung",
            ["UiPlantRefGrowth"] = "Wachstum",
            ["UiPlantRefPlacement"] = "Platzierung",
            ["UiPlantRefBehavior"] = "Verhalten",
            ["UiPlantRefCompatibility"] = "Vertraeglichkeit",
            ["UiPlantRefSourceUrl"] = "Quellen-URL",
            ["UiPlantRefSearchMore"] = "Weitere Suchen",
            ["UiPlantRefCheckCompatibility"] = "Kompatibilitaet pruefen",
            ["UiPlantRefEdit"] = "Referenz bearbeiten",
            ["UiPlantRefApplyFilters"] = "Filter anwenden",
            ["UiPlantRefResetFilters"] = "Filter zuruecksetzen",
            ["UiPlantRefDelete"] = "Referenz loeschen",
            ["UiPlantRefResetCatalog"] = "Katalog zuruecksetzen",
            ["UiPlantGrowth"] = "Wachstum",
            ["UiGrowthSlow"] = "Langsam",
            ["UiGrowthMedium"] = "Mittel",
            ["UiGrowthFast"] = "Schnell",
            ["UiPlantLightNeed"] = "Licht",
            ["UiPlantInventoryTotals"] = "Pflanzensumme je Art",
            ["UiLightLow"] = "Niedrig",
            ["UiLightMedium"] = "Mittel",
            ["UiLightHigh"] = "Stark",
            ["UiGridScientific"] = "Wissenschaftlich",
            ["UiGridGrowth"] = "Wachstum",
            ["UiButtonAddMeasurement"] = "Messung hinzufuegen",
            ["UiButtonDuplicateMeasurement"] = "Messung duplizieren",
            ["UiButtonDeleteMeasurement"] = "Messung loeschen",
            ["UiButtonAddPlant"] = "Pflanze hinzufuegen",
            ["UiButtonDeletePlant"] = "Pflanze loeschen",
            ["UiButtonAddPlantMovement"] = "Bewegung hinzufuegen",
            ["UiButtonDeletePlantMovement"] = "Zeile loeschen",
            ["UiPopulationSpecies"] = "Art",
            ["UiAnimalReferenceChoice"] = "Tierkatalog",
            ["UiPopulationType"] = "Typ",
            ["UiPopulationQuantity"] = "Menge",
            ["UiPopulationFamily"] = "Familie",
            ["UiPopulationInventoryTotals"] = "Besatzsumme je Art",
            ["UiPopulationTypeFish"] = "Fische",
            ["UiPopulationTypeShrimp"] = "Garnelen",
            ["UiPopulationTypeSnail"] = "Mollusken",
            ["UiPopulationTypeOther"] = "Andere",
            ["UiButtonAddPopulation"] = "Besatz hinzufuegen",
            ["UiButtonDeletePopulation"] = "Besatz loeschen",
            ["UiButtonAddPopulationMovement"] = "Bewegung hinzufuegen",
            ["UiButtonDeletePopulationMovement"] = "Zeile loeschen",
            ["UiInterventionDate"] = "Datum",
            ["UiInterventionTime"] = "Uhrzeit",
            ["UiInterventionType"] = "Typ",
            ["UiInterventionWaterChange"] = "Wasserwechsel",
            ["UiInterventionFertilization"] = "Duengung",
            ["UiInterventionFilterCleaning"] = "Filterreinigung",
            ["UiInterventionPopulationAdded"] = "Besatz hinzugefuegt",
            ["UiInterventionPopulationRemoved"] = "Besatz entfernt",
            ["UiInterventionMedicalTreatment"] = "Medizinische Behandlung",
            ["UiInterventionOther"] = "Sonstiges",
            ["UiInterventionProductName"] = "Produkt",
            ["UiInterventionProductQuantity"] = "Produktmenge",
            ["UiInterventionWaterVolume"] = "Ersetztes Wasser (L)",
            ["UiInterventionWaterPercent"] = "Ersetztes Wasser (%)",
            ["UiInterventionPopulationReason"] = "Besatzgrund",
            ["UiInterventionPopulationCount"] = "Individuen",
            ["UiButtonAddIntervention"] = "Eingriff hinzufuegen",
            ["UiButtonDeleteIntervention"] = "Eingriff loeschen",
            ["UiDbActionsHelp"] = "Datenbank- und Wartungsaktionen.",
            ["UiButtonInitializeMySql"] = "MySQL initialisieren",
            ["UiButtonSave"] = "Speichern",
            ["UiLabelLanguage"] = "Sprache",
            ["UiLabelTheme"] = "Design",
            ["UiSettingsDatabaseActions"] = "Datenbank",
            ["UiSettingsLocalization"] = "Sprache",
            ["UiSettingsAppearance"] = "Darstellung",
            ["UiApplicationLog"] = "Anwendungsprotokoll",
            ["UiButtonRefreshLog"] = "Protokoll aktualisieren",
            ["UiButtonOpenLog"] = "Protokoll oeffnen",
            ["UiLabelFontSize"] = "Schriftgroesse",
            ["UiFontSizeSmall"] = "Klein",
            ["UiFontSizeNormal"] = "Normal",
            ["UiFontSizeLarge"] = "Gross",
            ["UiLabelDensity"] = "Dichte",
            ["UiDensityCompact"] = "Kompakt",
            ["UiDensityComfortable"] = "Komfortabel",
            ["UiLabelAccentColor"] = "Akzentfarbe",
            ["UiAccentTeal"] = "Petrol",
            ["UiAccentBlue"] = "Blau",
            ["UiAccentGreen"] = "Gruen",
            ["UiAccentPurple"] = "Violett",
            ["UiHealthLastMeasure"] = "Letzte Messung",
            ["UiHealthGlobalStatus"] = "Gesamtstatus",
            ["UiHealthTrends"] = "Trends",
            ["UiHealthCharts"] = "Trenddiagramme",
            ["UiHealthPeriod"] = "Zeitraum",
            ["UiHealthPeriod7"] = "7 Tage",
            ["UiHealthPeriod30"] = "30 Tage",
            ["UiHealthPeriod90"] = "90 Tage",
            ["UiHealthPeriodAll"] = "Gesamte Historie",
            ["UiHealthParameters"] = "Anzuzeigende Parameter",
            ["UiHealthTargetRange"] = "Zielbereich",
            ["UiHealthNoChartData"] = "Nicht genug Messungen fuer ein Diagramm.",
            ["UiHealthActions"] = "Empfohlene Aktionen",
            ["UiHealthNoData"] = "Keine Messung fuer diesen Behaelter verfuegbar.",
            ["UiHealthParameterColumn"] = "Parameter",
            ["UiHealthValueColumn"] = "Wert",
            ["UiHealthTrendColumn"] = "Trend",
            ["UiHealthAlertColumn"] = "Warnung",
            ["HealthTrendNotAvailable"] = "N/A",
            ["HealthTrendUp"] = "Steigend",
            ["HealthTrendDown"] = "Fallend",
            ["HealthTrendStable"] = "Stabil",
            ["UiLangFrench"] = "Franzoesisch",
            ["UiLangEnglish"] = "Englisch",
            ["UiLangGerman"] = "Deutsch",
            ["UiThemeLight"] = "Hell",
            ["UiThemeDark"] = "Dunkel",
            ["UiFilterSearch"] = "Suche",
            ["UiFilterPeriod"] = "Zeitraum",
            ["UiFilterMovement"] = "Bewegung",
            ["UiFilterPopulationType"] = "Familie",
            ["UiFilterInterventionType"] = "Eingriffstyp",
            ["UiFilterAll"] = "Alle",
            ["UiMeasurementPeriod30"] = "30 Tage",
            ["UiMeasurementPeriod90"] = "90 Tage",
            ["UiClearFilters"] = "Filter loeschen",
            ["UiNoRowsMatchFilters"] = "Keine Zeile entspricht den Filtern.",
            ["UiReferenceSearchCriteriaTitle"] = "Suchkriterien",
            ["UiReferenceSearchCriteriaIntro"] = "Waehle die Kriterien, mit denen Kandidaten vor dem Einfuegen in den Katalog gefiltert werden.",
            ["UiReferenceSearchMinimumParameterGroups"] = "Mindestanzahl von Parametergruppen",
            ["UiReferenceSearchMinimumParameterGroupsHelp"] = "Wert von 1 bis 8. Je hoeher die Zahl, desto besser dokumentiert sind importierte Arten.",
            ["UiReferenceSearchInvalidMinimum"] = "Gib eine ganze Zahl von 1 bis 8 ein.",
            ["UiDialogOk"] = "OK",
            ["UiDialogCancel"] = "Abbrechen",
            ["StatusReady"] = "Bereit. MySQL ist beim Start optional, damit die App offline nutzbar bleibt.",
            ["StatusMySqlNotConfigured"] = "MySQL nicht konfiguriert. Nutze MySQL konfigurieren, um eine lokale Verbindung zu speichern.",
            ["StatusMySqlConfiguredSecure"] = "MySQL aus gesicherten lokalen Einstellungen konfiguriert.",
            ["StatusMySqlConfiguredEnv"] = "MySQL ueber ADAQUA_MYSQL_CONNECTION_STRING konfiguriert.",
            ["StatusReadFailed"] = "MySQL-Lesen nicht verfuegbar:",
            ["StatusMySqlConfigSaved"] = "MySQL-Konfiguration gespeichert und aktiv.",
            ["StatusConfigSavedReadFailed"] = "Konfiguration gespeichert, aber MySQL-Lesen fehlgeschlagen:",
            ["StatusConfigureBeforeInit"] = "MySQL vor der Schema-Initialisierung konfigurieren.",
            ["StatusSchemaInitialized"] = "MySQL-Schema initialisiert.",
            ["StatusInitializationFailed"] = "MySQL-Initialisierung fehlgeschlagen:",
            ["StatusConnectedNoAquarium"] = "MySQL verbunden. Noch keine Behaelter in der Datenbank.",
            ["StatusConnectedAquariumCount"] = "MySQL verbunden. {0} Behaelter geladen.",
            ["StatusLocalModeConfigure"] = "Lokaler Modus: MySQL konfigurieren, um in die Datenbank zu speichern.",
            ["StatusSaveInvalidInput"] = "Speichern fehlgeschlagen: Eingabefehler zuerst korrigieren.",
            ["StatusAquariumSaved"] = "Behaelter in MySQL gespeichert.",
            ["StatusSaveFailed"] = "MySQL-Speichern fehlgeschlagen:",
            ["StatusNewAquariumSaved"] = "Neuer Behaelter gespeichert.",
            ["StatusAquariumDeleted"] = "Behaelter geloescht.",
            ["StatusAquariumDeleteFailed"] = "Loeschen des Behaelters fehlgeschlagen:",
            ["StatusSelectMeasurementDelete"] = "Waehle eine Messung zum Loeschen aus.",
            ["StatusSelectMeasurementDuplicate"] = "Waehle eine Messung zum Duplizieren aus.",
            ["StatusMeasurementSaved"] = "Messung gespeichert.",
            ["StatusMeasurementDuplicated"] = "Messung dupliziert und gespeichert.",
            ["StatusMeasurementDeleted"] = "Messung geloescht.",
            ["StatusSelectPlantDelete"] = "Waehle eine Pflanze zum Loeschen aus.",
            ["StatusPlantSaved"] = "Pflanze gespeichert.",
            ["StatusPlantDeleted"] = "Pflanze geloescht.",
            ["StatusSelectPopulationDelete"] = "Waehle einen Besatzeintrag zum Loeschen aus.",
            ["StatusPopulationSaved"] = "Besatz gespeichert.",
            ["StatusPopulationDeleted"] = "Besatz geloescht.",
            ["StatusInterventionInvalidTime"] = "Ungueltige Eingriffszeit. Nutze das Format HH:mm.",
            ["StatusSelectInterventionDelete"] = "Waehle einen Eingriff zum Loeschen aus.",
            ["StatusInterventionSaved"] = "Eingriff gespeichert.",
            ["StatusInterventionDeleted"] = "Eingriff geloescht.",
            ["StatusLanguageChanged"] = "Sprache angewendet.",
            ["StatusThemeChanged"] = "Design angewendet.",
            ["StatusAppearanceChanged"] = "Darstellung angewendet.",
            ["HealthStatusNoData"] = "Keine Messungen",
            ["HealthStatusOk"] = "Stabil",
            ["HealthStatusWarning"] = "Mittlere Warnung",
            ["HealthStatusCritical"] = "Kritische Warnung",
            ["HealthAlertNoData"] = "N/A",
            ["HealthAlertOk"] = "OK",
            ["HealthAlertWarning"] = "Beobachten",
            ["HealthAlertCritical"] = "Kritisch",
            ["HealthActionNoData"] = "Fuege eine Wassermessung hinzu, um das Gesundheitsmonitoring zu aktivieren.",
            ["HealthActionOk"] = "Parameter im Zielbereich. Aktuelle Routine beibehalten.",
            ["HealthActionWarningNitrates"] = "Teilwasserwechsel einplanen, um Nitratwerte zu senken.",
            ["HealthActionWarningPh"] = "pH pruefen und bei Bedarf schrittweise anpassen.",
            ["HealthActionWarningHardness"] = "GH/KH pruefen und Wechselwasser anpassen.",
            ["HealthActionWarningGeneric"] = "Entwicklung bei den naechsten Messungen beobachten.",
            ["HealthActionCriticalWaterChange"] = "Schnellen Wasserwechsel durchfuehren und Filterung/Belueftung pruefen.",
            ["HealthActionCriticalFeeding"] = "Fuetterung voruebergehend reduzieren, um Stickstofflast zu senken.",
            ["HealthActionCriticalTemperature"] = "Temperatur (Heizen/Kuehlen) ohne abrupte Schwankung korrigieren.",
            ["HealthActionCriticalGeneric"] = "Wasser analysieren und kritische Parameter zuerst stabilisieren.",
            ["ConfirmDeleteTitle"] = "Loeschbestaetigung",
            ["ConfirmDeleteAquarium"] = "Behaelter \"{0}\" und alle zugehoerigen Daten loeschen?",
            ["ConfirmDeleteMeasurement"] = "Messung vom {0:g} loeschen?",
            ["ConfirmDeletePlant"] = "Pflanze \"{0}\" loeschen?",
            ["ConfirmDeletePopulation"] = "\"{0}\" aus dem Besatz loeschen?",
            ["ConfirmDeleteIntervention"] = "Eingriff vom {0:g} loeschen?",
            ["DefaultWaterType"] = "Suesswasser",
            ["DefaultMainAquarium"] = "Hauptbecken",
            ["DefaultMainAquariumNote"] = "Erster ADAqua-Behaelter.",
            ["DefaultPlantLight"] = "Niedrig",
            ["DefaultNeonName"] = "Neonsalmler"
        };

        return new Dictionary<string, Dictionary<string, string>>
        {
            [LanguageFrench] = fr,
            [LanguageEnglish] = en,
            [LanguageGerman] = de
        };
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private sealed record HealthRule(string ParameterKey, Func<WaterParameters, decimal?> Selector, decimal CriticalMin, decimal WarningMin, decimal WarningMax, decimal CriticalMax);

    private const string WaterTypeFreshwaterTropical = "FreshwaterTropical";
    private const string WaterTypeFreshwaterPond = "FreshwaterPond";
    private const string WaterTypeMarine = "Marine";
    private const string ContainerTypeAquarium = "Aquarium";
    private const string ContainerTypeFishPond = "FishPond";
    private const string FilterAllCode = "All";
    private const string MeasurementPeriod30Code = "30";
    private const string MeasurementPeriod90Code = "90";
    private const string ParameterAmmonia = "UiWaterParameterAmmonia";
    private const string ParameterNitrites = "UiWaterParameterNitrites";
    private const string ParameterNitrates = "UiWaterParameterNitrates";
    private const string ParameterPh = "UiWaterParameterPh";
    private const string ParameterGh = "UiWaterParameterGh";
    private const string ParameterKh = "UiWaterParameterKh";
    private const string ParameterTemperature = "UiWaterParameterTemperature";

    private Func<string, string> text = key => key;
    private string currentLanguage = "fr";
    private static readonly HealthRule[] FreshwaterHealthRules =
    [
        new(ParameterAmmonia, m => m.AmmoniaMgPerLiter, 0m, 0m, 0.05m, 0.2m),
        new(ParameterNitrites, m => m.NitritesMgPerLiter, 0m, 0m, 0.02m, 0.1m),
        new(ParameterNitrates, m => m.NitratesMgPerLiter, 0m, 0m, 25m, 40m),
        new(ParameterPh, m => m.Ph, 6m, 6.5m, 7.8m, 8.5m),
        new(ParameterGh, m => m.Gh, 1m, 4m, 12m, 20m),
        new(ParameterKh, m => m.Kh, 0m, 3m, 10m, 15m),
        new(ParameterTemperature, m => m.TemperatureCelsius, 18m, 22m, 27m, 30m)
    ];

    private static readonly HealthRule[] MarineHealthRules =
    [
        new(ParameterAmmonia, m => m.AmmoniaMgPerLiter, 0m, 0m, 0.02m, 0.1m),
        new(ParameterNitrites, m => m.NitritesMgPerLiter, 0m, 0m, 0.02m, 0.1m),
        new(ParameterNitrates, m => m.NitratesMgPerLiter, 0m, 0m, 20m, 50m),
        new(ParameterPh, m => m.Ph, 7.6m, 8.0m, 8.4m, 8.6m),
        new(ParameterGh, m => m.Gh, 6m, 8m, 20m, 30m),
        new(ParameterKh, m => m.Kh, 5m, 7m, 12m, 14m),
        new(ParameterTemperature, m => m.TemperatureCelsius, 22m, 24m, 27m, 30m)
    ];

    private static readonly HealthRule[] PondHealthRules =
    [
        new(ParameterAmmonia, m => m.AmmoniaMgPerLiter, 0m, 0m, 0.05m, 0.2m),
        new(ParameterNitrites, m => m.NitritesMgPerLiter, 0m, 0m, 0.02m, 0.1m),
        new(ParameterNitrates, m => m.NitratesMgPerLiter, 0m, 0m, 40m, 80m),
        new(ParameterPh, m => m.Ph, 6.5m, 7m, 8.5m, 9m),
        new(ParameterGh, m => m.Gh, 1m, 6m, 18m, 25m),
        new(ParameterKh, m => m.Kh, 3m, 5m, 14m, 18m),
        new(ParameterTemperature, m => m.TemperatureCelsius, 4m, 10m, 26m, 32m)
    ];

    private Aquarium selectedAquarium;
    private WaterParameters? selectedMeasurement;
    private AquariumPlant? selectedPlant;
    private PopulationMember? selectedPopulation;
    private AquariumIntervention? selectedIntervention;
    private PlantReferenceItem? selectedPlantReferenceForNewPlant;
    private AnimalReferenceItem? selectedAnimalReferenceForNewPopulation;
    private PlantReferenceItem? selectedPlantReference;
    private AnimalReferenceItem? selectedAnimalReference;
    private string statusMessage = string.Empty;
    private string healthLastMeasurementAt = "-";
    private string healthGlobalStatus = "-";
    private string applicationLogText = string.Empty;
    private string newInterventionTimeText = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
    private string plantReferenceEnvironmentLabel = string.Empty;
    private string plantFilterPhMin = string.Empty;
    private string plantFilterPhMax = string.Empty;
    private string plantFilterGhMin = string.Empty;
    private string plantFilterGhMax = string.Empty;
    private string plantFilterKhMin = string.Empty;
    private string plantFilterKhMax = string.Empty;
    private string plantFilterTempMin = string.Empty;
    private string plantFilterTempMax = string.Empty;
    private string plantFilterAmmoniaMax = string.Empty;
    private string plantFilterNitritesMax = string.Empty;
    private string plantFilterNitratesMax = string.Empty;
    private string animalReferenceEnvironmentLabel = string.Empty;
    private string animalFilterPhMin = string.Empty;
    private string animalFilterPhMax = string.Empty;
    private string animalFilterGhMin = string.Empty;
    private string animalFilterGhMax = string.Empty;
    private string animalFilterKhMin = string.Empty;
    private string animalFilterKhMax = string.Empty;
    private string animalFilterTempMin = string.Empty;
    private string animalFilterTempMax = string.Empty;
    private string animalFilterAmmoniaMax = string.Empty;
    private string animalFilterNitritesMax = string.Empty;
    private string animalFilterNitratesMax = string.Empty;
    private string animalReferenceGroupFilterCode = FilterAllCode;
    private string measurementSearchText = string.Empty;
    private string measurementPeriodFilterCode = FilterAllCode;
    private string plantSearchText = string.Empty;
    private string plantMovementFilterCode = FilterAllCode;
    private string populationSearchText = string.Empty;
    private string populationMovementFilterCode = FilterAllCode;
    private string populationTypeFilterCode = FilterAllCode;
    private string interventionSearchText = string.Empty;
    private string interventionTypeFilterCode = FilterAllCode;
    private ICollectionView? measurementsView;
    private ICollectionView? plantsView;
    private ICollectionView? populationView;
    private ICollectionView? interventionsView;
    private bool isGridRefreshQueued;
    private ContainerTypeOption? selectedAquariumContainerTypeOption;
    private WaterTypeOption? selectedAquariumWaterTypeOption;
    private int selectedTrendPeriodDays = 30;
    private int interventionLocalizationVersion;
    private int movementLocalizationVersion;
    private readonly List<PlantReferenceItem> plantReferenceCatalog = [];
    private readonly List<AnimalReferenceItem> animalReferenceCatalog = [];

    public MainWindowViewModel()
    {
        InitializeTrendParameterOptions();
        selectedAquarium = CreateDefaultAquarium();
        Aquariums.Add(selectedAquarium);
        UpdateContainerTypeOptions();
        UpdateWaterTypeOptions();
        UpdateInterventionTypeOptions();
        RebuildHealthDashboard();
        RebuildInventoryTotals();
        RebuildPlantReference();
        RebuildAnimalReference();
        UpdateGridFilterOptions();
        RebuildGridViews();
        StatusMessage = text("StatusReady");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectedAquariumWaterTypeChanged;
    public event EventHandler? SelectedAquariumContainerTypeChanged;

    public ObservableCollection<Aquarium> Aquariums { get; } = [];
    public WaterParameters NewMeasurement { get; private set; } = new();
    public AquariumPlant NewPlant { get; private set; } = new();
    public PopulationMember NewPopulation { get; private set; } = new();
    public AquariumIntervention NewIntervention { get; private set; } = new();
    public ObservableCollection<HealthIndicator> HealthIndicators { get; } = [];
    public ObservableCollection<TrendParameterOption> TrendParameterOptions { get; } = [];
    public ObservableCollection<HealthTrendSeries> HealthTrendSeries { get; } = [];
    public ObservableCollection<string> HealthRecommendedActions { get; } = [];
    public ObservableCollection<PlantReferenceItem> PlantReferenceChoices { get; } = [];
    public ObservableCollection<PlantReferenceItem> PlantReferencesFiltered { get; } = [];
    public ObservableCollection<AnimalReferenceItem> AnimalReferenceChoices { get; } = [];
    public ObservableCollection<AnimalReferenceItem> AnimalReferencesFiltered { get; } = [];
    public ObservableCollection<PlantInventoryTotal> PlantInventoryTotals { get; } = [];
    public ObservableCollection<PopulationInventoryTotal> PopulationInventoryTotals { get; } = [];
    public ObservableCollection<ContainerTypeOption> ContainerTypeOptions { get; } = [];
    public ObservableCollection<WaterTypeOption> WaterTypeOptions { get; } = [];
    public ObservableCollection<InterventionTypeOption> InterventionTypeOptions { get; } = [];
    public ObservableCollection<FilterOption> MeasurementPeriodFilterOptions { get; } = [];
    public ObservableCollection<FilterOption> MovementFilterOptions { get; } = [];
    public ObservableCollection<FilterOption> PopulationTypeFilterOptions { get; } = [];
    public ObservableCollection<FilterOption> AnimalReferenceGroupFilterOptions { get; } = [];
    public ObservableCollection<FilterOption> InterventionTypeFilterOptions { get; } = [];
    public int InterventionLocalizationVersion => interventionLocalizationVersion;
    public int MovementLocalizationVersion => movementLocalizationVersion;
    public ICollectionView? MeasurementsView
    {
        get => measurementsView;
        private set => SetField(ref measurementsView, value);
    }

    public ICollectionView? PlantsView
    {
        get => plantsView;
        private set => SetField(ref plantsView, value);
    }

    public ICollectionView? PopulationView
    {
        get => populationView;
        private set => SetField(ref populationView, value);
    }

    public ICollectionView? InterventionsView
    {
        get => interventionsView;
        private set => SetField(ref interventionsView, value);
    }

    public bool HasNoMeasurementResults => MeasurementsView?.IsEmpty == true;
    public bool HasNoPlantResults => PlantsView?.IsEmpty == true;
    public bool HasNoPopulationResults => PopulationView?.IsEmpty == true;
    public bool HasNoInterventionResults => InterventionsView?.IsEmpty == true;

    public Aquarium SelectedAquarium
    {
        get => selectedAquarium;
        set
        {
            if (value is null)
            {
                return;
            }

            var normalizedWaterType = NormalizeWaterTypeCode(value.WaterType);
            var normalizedContainerType = NormalizeContainerTypeCode(value.ContainerType);
            var classificationChanged = value.WaterType != normalizedWaterType || value.ContainerType != normalizedContainerType;
            value.WaterType = normalizedWaterType;
            value.ContainerType = normalizedContainerType;
            if (IsFishPondContainerType(value.ContainerType))
            {
                classificationChanged |= value.WaterType != WaterTypeFreshwaterTropical;
                value.WaterType = WaterTypeFreshwaterTropical;
            }

            if (SetField(ref selectedAquarium, value))
            {
                SelectedMeasurement = null;
                SelectedPlant = null;
                SelectedPopulation = null;
                SelectedIntervention = null;
                SelectedPlantReferenceForNewPlant = null;
                SelectedAnimalReferenceForNewPopulation = null;
                OnPropertyChanged(nameof(StartedOnDateTime));
                UpdateWaterTypeOptions();
                SyncSelectedAquariumContainerTypeOption();
                OnPropertyChanged(nameof(SelectedAquariumContainerType));
                OnPropertyChanged(nameof(SelectedAquariumWaterType));
                OnPropertyChanged(nameof(IsSelectedAquariumMarine));
                OnPropertyChanged(nameof(IsSelectedContainerFishPond));
                RebuildHealthDashboard();
                RebuildInventoryTotals();
                RebuildPlantReference();
                RebuildAnimalReference();
                RebuildGridViews();
            }
            else if (classificationChanged)
            {
                RefreshSelectedAquariumClassificationBindings();
            }
        }
    }

    public WaterParameters? SelectedMeasurement
    {
        get => selectedMeasurement;
        set => SetField(ref selectedMeasurement, value);
    }

    public AquariumPlant? SelectedPlant
    {
        get => selectedPlant;
        set => SetField(ref selectedPlant, value);
    }

    public PopulationMember? SelectedPopulation
    {
        get => selectedPopulation;
        set => SetField(ref selectedPopulation, value);
    }

    public PopulationType NewPopulationType
    {
        get => NewPopulation.Type;
        set
        {
            if (NewPopulation.Type == value)
            {
                return;
            }

            NewPopulation.Type = value;
            SelectedAnimalReferenceForNewPopulation = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewPopulation));
            RebuildAnimalReferenceChoices();
        }
    }

    public AquariumIntervention? SelectedIntervention
    {
        get => selectedIntervention;
        set => SetField(ref selectedIntervention, value);
    }

    public PlantReferenceItem? SelectedPlantReferenceForNewPlant
    {
        get => selectedPlantReferenceForNewPlant;
        set
        {
            if (SetField(ref selectedPlantReferenceForNewPlant, value) && value is not null)
            {
                ApplyPlantReferenceToNewPlant(value);
            }
        }
    }

    public AnimalReferenceItem? SelectedAnimalReferenceForNewPopulation
    {
        get => selectedAnimalReferenceForNewPopulation;
        set
        {
            if (SetField(ref selectedAnimalReferenceForNewPopulation, value) && value is not null)
            {
                ApplyAnimalReferenceToNewPopulation(value);
            }
        }
    }

    public PlantReferenceItem? SelectedPlantReference
    {
        get => selectedPlantReference;
        set => SetField(ref selectedPlantReference, value);
    }

    public AnimalReferenceItem? SelectedAnimalReference
    {
        get => selectedAnimalReference;
        set => SetField(ref selectedAnimalReference, value);
    }

    public ContainerTypeOption? SelectedAquariumContainerTypeOption
    {
        get => selectedAquariumContainerTypeOption;
        set
        {
            if (value is null)
            {
                SyncSelectedAquariumContainerTypeOption();
                OnPropertyChanged();
                return;
            }

            if (!ReferenceEquals(selectedAquariumContainerTypeOption, value))
            {
                selectedAquariumContainerTypeOption = value;
                OnPropertyChanged();
            }

            SelectedAquariumContainerType = value.Code;
        }
    }

    public string SelectedAquariumContainerType
    {
        get => NormalizeContainerTypeCode(SelectedAquarium.ContainerType);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged();
                return;
            }

            var normalized = NormalizeContainerTypeCode(value);
            if (SelectedAquarium.ContainerType == normalized)
            {
                SyncSelectedAquariumContainerTypeOption();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
                return;
            }

            SelectedAquarium.ContainerType = normalized;
            if (IsFishPondContainerType(normalized))
            {
                SelectedAquarium.WaterType = WaterTypeFreshwaterTropical;
            }

            UpdateWaterTypeOptions();
            SyncSelectedAquariumContainerTypeOption();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
            OnPropertyChanged(nameof(SelectedAquariumWaterType));
            OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
            OnPropertyChanged(nameof(SelectedAquarium));
            OnPropertyChanged(nameof(IsSelectedAquariumMarine));
            OnPropertyChanged(nameof(IsSelectedContainerFishPond));
            RebuildHealthDashboard();
            RebuildPlantReferenceChoices();
            RebuildPlantReference();
            RebuildAnimalReference();
            SelectedAquariumContainerTypeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public WaterTypeOption? SelectedAquariumWaterTypeOption
    {
        get => selectedAquariumWaterTypeOption;
        set
        {
            if (value is null)
            {
                SyncSelectedAquariumWaterTypeOption();
                OnPropertyChanged();
                return;
            }

            if (!ReferenceEquals(selectedAquariumWaterTypeOption, value))
            {
                selectedAquariumWaterTypeOption = value;
                OnPropertyChanged();
            }

            SelectedAquariumWaterType = value.Code;
        }
    }

    public string SelectedAquariumWaterType
    {
        get => IsSelectedContainerFishPond ? WaterTypeFreshwaterPond : NormalizeWaterTypeCode(SelectedAquarium.WaterType);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged();
                return;
            }

            var previousWaterType = SelectedAquarium.WaterType;
            var previousContainerType = SelectedAquarium.ContainerType;
            var selectedPondWater = string.Equals(value, WaterTypeFreshwaterPond, StringComparison.OrdinalIgnoreCase);
            var normalized = selectedPondWater ? WaterTypeFreshwaterTropical : NormalizeWaterTypeCode(value);
            SelectedAquarium.ContainerType = selectedPondWater ? ContainerTypeFishPond : ContainerTypeAquarium;
            if (selectedPondWater)
            {
                normalized = WaterTypeFreshwaterTropical;
            }

            if (SelectedAquarium.WaterType == normalized && SelectedAquarium.ContainerType == previousContainerType)
            {
                SyncSelectedAquariumWaterTypeOption();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
                return;
            }

            SelectedAquarium.WaterType = normalized;
            SyncSelectedAquariumContainerTypeOption();
            SyncSelectedAquariumWaterTypeOption();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAquariumContainerType));
            OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
            OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
            OnPropertyChanged(nameof(SelectedAquarium));
            OnPropertyChanged(nameof(IsSelectedAquariumMarine));
            OnPropertyChanged(nameof(IsSelectedContainerFishPond));
            RebuildHealthDashboard();
            RebuildPlantReferenceChoices();
            RebuildPlantReference();
            RebuildAnimalReference();
            if (SelectedAquarium.ContainerType != previousContainerType)
            {
                SelectedAquariumContainerTypeChanged?.Invoke(this, EventArgs.Empty);
            }

            if (SelectedAquarium.WaterType != previousWaterType)
            {
                SelectedAquariumWaterTypeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public DateTime StartedOnDateTime
    {
        get => SelectedAquarium.StartedOn.ToDateTime(TimeOnly.MinValue);
        set
        {
            SelectedAquarium.StartedOn = DateOnly.FromDateTime(value);
            OnPropertyChanged();
        }
    }

    public bool IsSelectedAquariumMarine => IsMarineWaterType(SelectedAquarium.WaterType);
    public bool IsSelectedContainerFishPond => IsFishPondContainerType(SelectedAquarium.ContainerType);

    public string StatusMessage
    {
        get => statusMessage;
        set => SetField(ref statusMessage, value);
    }

    public string HealthLastMeasurementAt
    {
        get => healthLastMeasurementAt;
        private set => SetField(ref healthLastMeasurementAt, value);
    }

    public string HealthGlobalStatus
    {
        get => healthGlobalStatus;
        private set => SetField(ref healthGlobalStatus, value);
    }

    public string ApplicationLogText
    {
        get => applicationLogText;
        set => SetField(ref applicationLogText, value);
    }

    public string NewInterventionTimeText
    {
        get => newInterventionTimeText;
        set => SetField(ref newInterventionTimeText, value);
    }

    public string AnimalReferenceEnvironmentLabel
    {
        get => animalReferenceEnvironmentLabel;
        private set => SetField(ref animalReferenceEnvironmentLabel, value);
    }

    public string AnimalFilterPhMin { get => animalFilterPhMin; set => SetField(ref animalFilterPhMin, value); }
    public string AnimalFilterPhMax { get => animalFilterPhMax; set => SetField(ref animalFilterPhMax, value); }
    public string AnimalFilterGhMin { get => animalFilterGhMin; set => SetField(ref animalFilterGhMin, value); }
    public string AnimalFilterGhMax { get => animalFilterGhMax; set => SetField(ref animalFilterGhMax, value); }
    public string AnimalFilterKhMin { get => animalFilterKhMin; set => SetField(ref animalFilterKhMin, value); }
    public string AnimalFilterKhMax { get => animalFilterKhMax; set => SetField(ref animalFilterKhMax, value); }
    public string AnimalFilterTempMin { get => animalFilterTempMin; set => SetField(ref animalFilterTempMin, value); }
    public string AnimalFilterTempMax { get => animalFilterTempMax; set => SetField(ref animalFilterTempMax, value); }
    public string AnimalFilterAmmoniaMax { get => animalFilterAmmoniaMax; set => SetField(ref animalFilterAmmoniaMax, value); }
    public string AnimalFilterNitritesMax { get => animalFilterNitritesMax; set => SetField(ref animalFilterNitritesMax, value); }
    public string AnimalFilterNitratesMax { get => animalFilterNitratesMax; set => SetField(ref animalFilterNitratesMax, value); }

    public string AnimalReferenceGroupFilterCode
    {
        get => animalReferenceGroupFilterCode;
        set
        {
            var normalized = NormalizeAnimalReferenceGroupFilterCode(value);
            if (SetField(ref animalReferenceGroupFilterCode, normalized))
            {
                RebuildAnimalReference();
            }
        }
    }

    public string PlantReferenceEnvironmentLabel
    {
        get => plantReferenceEnvironmentLabel;
        private set => SetField(ref plantReferenceEnvironmentLabel, value);
    }

    public string PlantFilterPhMin { get => plantFilterPhMin; set => SetField(ref plantFilterPhMin, value); }
    public string PlantFilterPhMax { get => plantFilterPhMax; set => SetField(ref plantFilterPhMax, value); }
    public string PlantFilterGhMin { get => plantFilterGhMin; set => SetField(ref plantFilterGhMin, value); }
    public string PlantFilterGhMax { get => plantFilterGhMax; set => SetField(ref plantFilterGhMax, value); }
    public string PlantFilterKhMin { get => plantFilterKhMin; set => SetField(ref plantFilterKhMin, value); }
    public string PlantFilterKhMax { get => plantFilterKhMax; set => SetField(ref plantFilterKhMax, value); }
    public string PlantFilterTempMin { get => plantFilterTempMin; set => SetField(ref plantFilterTempMin, value); }
    public string PlantFilterTempMax { get => plantFilterTempMax; set => SetField(ref plantFilterTempMax, value); }
    public string PlantFilterAmmoniaMax { get => plantFilterAmmoniaMax; set => SetField(ref plantFilterAmmoniaMax, value); }
    public string PlantFilterNitritesMax { get => plantFilterNitritesMax; set => SetField(ref plantFilterNitritesMax, value); }
    public string PlantFilterNitratesMax { get => plantFilterNitratesMax; set => SetField(ref plantFilterNitratesMax, value); }

    public string MeasurementSearchText
    {
        get => measurementSearchText;
        set
        {
            if (SetField(ref measurementSearchText, value ?? string.Empty))
            {
                RefreshGridViews();
            }
        }
    }

    public string MeasurementPeriodFilterCode
    {
        get => measurementPeriodFilterCode;
        set
        {
            var normalized = value is MeasurementPeriod30Code or MeasurementPeriod90Code ? value : FilterAllCode;
            if (SetField(ref measurementPeriodFilterCode, normalized))
            {
                RefreshGridViews();
            }
        }
    }

    public string PlantSearchText
    {
        get => plantSearchText;
        set
        {
            if (SetField(ref plantSearchText, value ?? string.Empty))
            {
                RefreshGridViews();
            }
        }
    }

    public string PlantMovementFilterCode
    {
        get => plantMovementFilterCode;
        set
        {
            var normalized = NormalizeMovementFilterCode(value);
            if (SetField(ref plantMovementFilterCode, normalized))
            {
                RefreshGridViews();
            }
        }
    }

    public string PopulationSearchText
    {
        get => populationSearchText;
        set
        {
            if (SetField(ref populationSearchText, value ?? string.Empty))
            {
                RefreshGridViews();
            }
        }
    }

    public string PopulationMovementFilterCode
    {
        get => populationMovementFilterCode;
        set
        {
            var normalized = NormalizeMovementFilterCode(value);
            if (SetField(ref populationMovementFilterCode, normalized))
            {
                RefreshGridViews();
            }
        }
    }

    public string PopulationTypeFilterCode
    {
        get => populationTypeFilterCode;
        set
        {
            var normalized = NormalizePopulationTypeFilterCode(value);
            if (SetField(ref populationTypeFilterCode, normalized))
            {
                RefreshGridViews();
            }
        }
    }

    public string InterventionSearchText
    {
        get => interventionSearchText;
        set
        {
            if (SetField(ref interventionSearchText, value ?? string.Empty))
            {
                RefreshGridViews();
            }
        }
    }

    public string InterventionTypeFilterCode
    {
        get => interventionTypeFilterCode;
        set
        {
            var normalized = NormalizeInterventionTypeFilterCode(value);
            if (SetField(ref interventionTypeFilterCode, normalized))
            {
                RefreshGridViews();
            }
        }
    }

    public int SelectedTrendPeriodDays
    {
        get => selectedTrendPeriodDays;
        set
        {
            var normalized = NormalizeTrendPeriod(value);
            if (SetField(ref selectedTrendPeriodDays, normalized))
            {
                RebuildHealthDashboard();
            }
        }
    }

    public void SetPlantReferences(IReadOnlyList<PlantReference> references)
    {
        plantReferenceCatalog.Clear();
        foreach (var reference in references)
        {
            plantReferenceCatalog.Add(new PlantReferenceItem(
                reference.Id,
                reference.Environment,
                reference.CommonName,
                reference.CommonNameFr,
                reference.CommonNameEn,
                reference.CommonNameDe,
                reference.ScientificName,
                reference.PhMin,
                reference.PhMax,
                reference.GhMin,
                reference.GhMax,
                reference.KhMin,
                reference.KhMax,
                reference.TemperatureMin,
                reference.TemperatureMax,
                reference.AmmoniaMin,
                reference.AmmoniaMax,
                reference.NitritesMin,
                reference.NitritesMax,
                reference.NitratesMin,
                reference.NitratesMax,
                reference.VolumeMinLiters,
                reference.LightNeed,
                reference.Co2Need,
                reference.FertilizationNeed,
                reference.GrowthSpeed,
                reference.RecommendedPlacement,
                reference.Behavior,
                reference.Compatibility,
                reference.SourceUrl,
                currentLanguage));
        }

        RebuildPlantReference();
    }

    public void SetAnimalReferences(IReadOnlyList<AnimalReference> references)
    {
        animalReferenceCatalog.Clear();
        foreach (var reference in references)
        {
            animalReferenceCatalog.Add(new AnimalReferenceItem(
                reference.Id,
                reference.Environment,
                reference.Group,
                reference.CommonName,
                reference.CommonNameFr,
                reference.CommonNameEn,
                reference.CommonNameDe,
                reference.ScientificName,
                reference.PhMin,
                reference.PhMax,
                reference.GhMin,
                reference.GhMax,
                reference.KhMin,
                reference.KhMax,
                reference.TemperatureMin,
                reference.TemperatureMax,
                reference.AmmoniaMin,
                reference.AmmoniaMax,
                reference.NitritesMin,
                reference.NitritesMax,
                reference.NitratesMin,
                reference.NitratesMax,
                reference.VolumeMinLiters,
                reference.Behavior,
                reference.Compatibility,
                reference.SourceUrl,
                currentLanguage));
        }

        RebuildAnimalReference();
    }

    public void ApplyPlantReferenceCompatibilityHighlight()
    {
        var latestMeasurement = SelectedAquarium.Measurements
            .OrderByDescending(m => m.MeasuredAt)
            .FirstOrDefault();

        foreach (var item in PlantReferencesFiltered)
        {
            if (latestMeasurement is null)
            {
                item.RowBackgroundBrush = Brushes.Transparent;
                item.IsPhIncompatible = false;
                item.IsGhIncompatible = false;
                item.IsKhIncompatible = false;
                item.IsTemperatureIncompatible = false;
                item.IsAmmoniaIncompatible = false;
                item.IsNitritesIncompatible = false;
                item.IsNitratesIncompatible = false;
                continue;
            }

            item.IsPhIncompatible = !IsWithin(latestMeasurement.Ph, item.PhMin, item.PhMax);
            item.IsGhIncompatible = !IsWithin(latestMeasurement.Gh, item.GhMin, item.GhMax);
            item.IsKhIncompatible = !IsWithin(latestMeasurement.Kh, item.KhMin, item.KhMax);
            item.IsTemperatureIncompatible = !IsWithin(latestMeasurement.TemperatureCelsius, item.TemperatureMin, item.TemperatureMax);
            item.IsAmmoniaIncompatible = !IsWithin(latestMeasurement.AmmoniaMgPerLiter, item.AmmoniaMin, item.AmmoniaMax);
            item.IsNitritesIncompatible = !IsWithin(latestMeasurement.NitritesMgPerLiter, item.NitritesMin, item.NitritesMax);
            item.IsNitratesIncompatible = !IsWithin(latestMeasurement.NitratesMgPerLiter, item.NitratesMin, item.NitratesMax);

            var isCompatible = !item.IsPhIncompatible
                && !item.IsGhIncompatible
                && !item.IsKhIncompatible
                && !item.IsTemperatureIncompatible
                && !item.IsAmmoniaIncompatible
                && !item.IsNitritesIncompatible
                && !item.IsNitratesIncompatible;

            item.RowBackgroundBrush = isCompatible
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#203F2A"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A1F1F"));
        }
    }

    public void ApplyAnimalReferenceCompatibilityHighlight()
    {
        var latestMeasurement = SelectedAquarium.Measurements
            .OrderByDescending(m => m.MeasuredAt)
            .FirstOrDefault();

        foreach (var item in AnimalReferencesFiltered)
        {
            if (latestMeasurement is null)
            {
                item.RowBackgroundBrush = Brushes.Transparent;
                item.IsPhIncompatible = false;
                item.IsGhIncompatible = false;
                item.IsKhIncompatible = false;
                item.IsTemperatureIncompatible = false;
                item.IsAmmoniaIncompatible = false;
                item.IsNitritesIncompatible = false;
                item.IsNitratesIncompatible = false;
                continue;
            }

            item.IsPhIncompatible = !IsWithin(latestMeasurement.Ph, item.PhMin, item.PhMax);
            item.IsGhIncompatible = !IsWithin(latestMeasurement.Gh, item.GhMin, item.GhMax);
            item.IsKhIncompatible = !IsWithin(latestMeasurement.Kh, item.KhMin, item.KhMax);
            item.IsTemperatureIncompatible = !IsWithin(latestMeasurement.TemperatureCelsius, item.TemperatureMin, item.TemperatureMax);
            item.IsAmmoniaIncompatible = !IsWithin(latestMeasurement.AmmoniaMgPerLiter, item.AmmoniaMin, item.AmmoniaMax);
            item.IsNitritesIncompatible = !IsWithin(latestMeasurement.NitritesMgPerLiter, item.NitritesMin, item.NitritesMax);
            item.IsNitratesIncompatible = !IsWithin(latestMeasurement.NitratesMgPerLiter, item.NitratesMin, item.NitratesMax);

            var isCompatible = !item.IsPhIncompatible
                && !item.IsGhIncompatible
                && !item.IsKhIncompatible
                && !item.IsTemperatureIncompatible
                && !item.IsAmmoniaIncompatible
                && !item.IsNitritesIncompatible
                && !item.IsNitratesIncompatible;

            item.RowBackgroundBrush = isCompatible
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#203F2A"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A1F1F"));
        }
    }

    public void SetTextProvider(Func<string, string> provider)
    {
        text = provider;
        UpdateContainerTypeOptions();
        UpdateWaterTypeOptions();
        UpdateInterventionTypeOptions();
        UpdateMovementLocalization();
        UpdateGridFilterOptions();
        RebuildInventoryTotals();
        if (string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = text("StatusReady");
        }
    }

    public void SetLanguage(string languageCode)
    {
        currentLanguage = languageCode is "en" or "de" ? languageCode : "fr";
        UpdateContainerTypeOptions();
        UpdateWaterTypeOptions();
        UpdateInterventionTypeOptions();
        UpdateTrendParameterOptionLabels();
        foreach (var item in plantReferenceCatalog)
        {
            item.SetLanguage(currentLanguage);
        }

        foreach (var item in PlantReferencesFiltered)
        {
            item.SetLanguage(currentLanguage);
        }

        foreach (var item in PlantReferenceChoices)
        {
            item.SetLanguage(currentLanguage);
        }

        foreach (var item in animalReferenceCatalog)
        {
            item.SetLanguage(currentLanguage);
        }

        foreach (var item in AnimalReferencesFiltered)
        {
            item.SetLanguage(currentLanguage);
        }

        foreach (var item in AnimalReferenceChoices)
        {
            item.SetLanguage(currentLanguage);
        }

        UpdateMovementLocalization();
        UpdateGridFilterOptions();
        RebuildInventoryTotals();
        RefreshGridViews();
    }

    private void UpdateContainerTypeOptions()
    {
        ContainerTypeOptions.Clear();
        ContainerTypeOptions.Add(new ContainerTypeOption(ContainerTypeAquarium, text("UiContainerTypeAquarium")));
        ContainerTypeOptions.Add(new ContainerTypeOption(ContainerTypeFishPond, text("UiContainerTypeFishPond")));
        SyncSelectedAquariumContainerTypeOption();
        OnPropertyChanged(nameof(ContainerTypeOptions));
        OnPropertyChanged(nameof(SelectedAquariumContainerType));
        OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
    }

    private void UpdateWaterTypeOptions()
    {
        WaterTypeOptions.Clear();
        WaterTypeOptions.Add(new WaterTypeOption(WaterTypeFreshwaterTropical, text("UiWaterTypeFreshwaterTropical")));
        WaterTypeOptions.Add(new WaterTypeOption(WaterTypeFreshwaterPond, text("UiWaterTypeFreshwaterPond")));
        WaterTypeOptions.Add(new WaterTypeOption(WaterTypeMarine, text("UiWaterTypeMarine")));

        SyncSelectedAquariumWaterTypeOption();
        OnPropertyChanged(nameof(WaterTypeOptions));
        OnPropertyChanged(nameof(SelectedAquariumWaterType));
        OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
    }

    private void SyncSelectedAquariumContainerTypeOption()
    {
        var option = ContainerTypeOptions.FirstOrDefault(candidate => candidate.Code == SelectedAquariumContainerType);
        if (!ReferenceEquals(selectedAquariumContainerTypeOption, option))
        {
            selectedAquariumContainerTypeOption = option;
            OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
        }
    }

    private void SyncSelectedAquariumWaterTypeOption()
    {
        var selectedCode = IsSelectedContainerFishPond
            ? WaterTypeFreshwaterPond
            : SelectedAquariumWaterType;
        var option = WaterTypeOptions.FirstOrDefault(candidate => candidate.Code == selectedCode);
        if (!ReferenceEquals(selectedAquariumWaterTypeOption, option))
        {
            selectedAquariumWaterTypeOption = option;
            OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
        }
    }

    private void RefreshSelectedAquariumClassificationBindings()
    {
        SyncSelectedAquariumContainerTypeOption();
        UpdateWaterTypeOptions();
        OnPropertyChanged(nameof(SelectedAquariumContainerType));
        OnPropertyChanged(nameof(SelectedAquariumContainerTypeOption));
        OnPropertyChanged(nameof(SelectedAquariumWaterType));
        OnPropertyChanged(nameof(SelectedAquariumWaterTypeOption));
        OnPropertyChanged(nameof(SelectedAquarium));
        OnPropertyChanged(nameof(IsSelectedAquariumMarine));
        OnPropertyChanged(nameof(IsSelectedContainerFishPond));
    }

    public void RefreshSelectedAquariumClassificationAfterPersist()
    {
        RefreshSelectedAquariumClassificationBindings();
    }

    private void UpdateInterventionTypeOptions()
    {
        InterventionTypeOptions.Clear();
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.WaterChange, text("UiInterventionWaterChange")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.Fertilization, text("UiInterventionFertilization")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.FilterCleaning, text("UiInterventionFilterCleaning")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.PopulationAdded, text("UiInterventionPopulationAdded")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.PopulationRemoved, text("UiInterventionPopulationRemoved")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.MedicalTreatment, text("UiInterventionMedicalTreatment")));
        InterventionTypeOptions.Add(new InterventionTypeOption(InterventionType.Other, text("UiInterventionOther")));
        interventionLocalizationVersion++;
        OnPropertyChanged(nameof(InterventionTypeOptions));
        OnPropertyChanged(nameof(InterventionLocalizationVersion));
    }

    private void UpdateMovementLocalization()
    {
        movementLocalizationVersion++;
        OnPropertyChanged(nameof(MovementLocalizationVersion));
    }

    private void UpdateGridFilterOptions()
    {
        ResetOptions(
            MeasurementPeriodFilterOptions,
            new FilterOption(FilterAllCode, text("UiFilterAll")),
            new FilterOption(MeasurementPeriod30Code, text("UiMeasurementPeriod30")),
            new FilterOption(MeasurementPeriod90Code, text("UiMeasurementPeriod90")));

        ResetOptions(
            MovementFilterOptions,
            new FilterOption(FilterAllCode, text("UiFilterAll")),
            new FilterOption(nameof(InventoryMovementType.Addition), text("UiMovementAddition")),
            new FilterOption(nameof(InventoryMovementType.Removal), text("UiMovementRemoval")));

        ResetOptions(
            PopulationTypeFilterOptions,
            new FilterOption(FilterAllCode, text("UiFilterAll")),
            new FilterOption(nameof(PopulationType.Fish), text("UiPopulationTypeFish")),
            new FilterOption(nameof(PopulationType.Shrimp), text("UiPopulationTypeShrimp")),
            new FilterOption(nameof(PopulationType.Snail), text("UiPopulationTypeSnail")),
            new FilterOption(nameof(PopulationType.Other), text("UiPopulationTypeOther")));

        ResetOptions(
            AnimalReferenceGroupFilterOptions,
            new FilterOption(FilterAllCode, text("UiFilterAll")),
            new FilterOption(nameof(AnimalReferenceGroup.Fish), text("UiAnimalGroupFish")),
            new FilterOption(nameof(AnimalReferenceGroup.Shrimp), text("UiAnimalGroupShrimp")),
            new FilterOption(nameof(AnimalReferenceGroup.Snail), text("UiAnimalGroupSnail")),
            new FilterOption(nameof(AnimalReferenceGroup.Other), text("UiAnimalGroupOther")));

        ResetOptions(
            InterventionTypeFilterOptions,
            new FilterOption(FilterAllCode, text("UiFilterAll")),
            new FilterOption(nameof(InterventionType.WaterChange), text("UiInterventionWaterChange")),
            new FilterOption(nameof(InterventionType.Fertilization), text("UiInterventionFertilization")),
            new FilterOption(nameof(InterventionType.FilterCleaning), text("UiInterventionFilterCleaning")),
            new FilterOption(nameof(InterventionType.PopulationAdded), text("UiInterventionPopulationAdded")),
            new FilterOption(nameof(InterventionType.PopulationRemoved), text("UiInterventionPopulationRemoved")),
            new FilterOption(nameof(InterventionType.MedicalTreatment), text("UiInterventionMedicalTreatment")),
            new FilterOption(nameof(InterventionType.Other), text("UiInterventionOther")));

        OnPropertyChanged(nameof(MeasurementPeriodFilterOptions));
        OnPropertyChanged(nameof(MovementFilterOptions));
        OnPropertyChanged(nameof(PopulationTypeFilterOptions));
        OnPropertyChanged(nameof(AnimalReferenceGroupFilterOptions));
        OnPropertyChanged(nameof(InterventionTypeFilterOptions));
    }

    private static void ResetOptions(ObservableCollection<FilterOption> target, params FilterOption[] options)
    {
        target.Clear();
        foreach (var option in options)
        {
            target.Add(option);
        }
    }

    public void NotifyLanguageChanged()
    {
        NormalizeAllAquariumClassifications();

        UpdateContainerTypeOptions();
        UpdateWaterTypeOptions();
        UpdateTrendParameterOptionLabels();
        RebuildHealthDashboard();
        RebuildInventoryTotals();
        OnPropertyChanged(nameof(SelectedAquarium));
        RebuildPlantReferenceChoices();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    private void NormalizeAllAquariumClassifications()
    {
        foreach (var aquarium in Aquariums)
        {
            NormalizeAquariumClassification(aquarium);
        }
    }

    public void AddAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = $"Aquarium {Aquariums.Count + 1}",
            VolumeLiters = 60,
            ContainerType = ContainerTypeAquarium,
            WaterType = WaterTypeFreshwaterTropical
        };

        Aquariums.Add(aquarium);
        SelectedAquarium = aquarium;
        StatusMessage = text("StatusNewAquariumSaved");
        RebuildHealthDashboard();
        RebuildInventoryTotals();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    public void DeleteSelectedAquarium()
    {
        var aquarium = SelectedAquarium;
        var index = Aquariums.IndexOf(aquarium);
        Aquariums.Remove(aquarium);

        if (Aquariums.Count == 0)
        {
            AddAquarium();
            return;
        }

        SelectedAquarium = Aquariums[Math.Clamp(index, 0, Aquariums.Count - 1)];
        RebuildHealthDashboard();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    public void ReplaceAquariums(IReadOnlyList<Aquarium> aquariums)
    {
        Aquariums.Clear();
        foreach (var aquarium in aquariums)
        {
            NormalizeAquariumClassification(aquarium);
            SortMeasurementsDescending(aquarium);
            SortInterventionsDescending(aquarium);
            NormalizeInventoryEntries(aquarium);
            SortInventoryMovementsAscending(aquarium);
            Aquariums.Add(aquarium);
        }

        if (Aquariums.Count == 0)
        {
            AddAquarium();
            return;
        }

        SelectedAquarium = Aquariums[0];
        RebuildHealthDashboard();
        RebuildInventoryTotals();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    public void SelectAquarium(Guid aquariumId)
    {
        var aquarium = Aquariums.FirstOrDefault(candidate => candidate.Id == aquariumId);
        if (aquarium is not null)
        {
            NormalizeAquariumClassification(aquarium);
            SortMeasurementsDescending(aquarium);
            SortInterventionsDescending(aquarium);
            NormalizeInventoryEntries(aquarium);
            SortInventoryMovementsAscending(aquarium);
            SelectedAquarium = aquarium;
            RebuildHealthDashboard();
            RebuildInventoryTotals();
            RebuildPlantReference();
            RebuildAnimalReference();
        }
    }

    public void AddMeasurement()
    {
        NewMeasurement.MeasuredAt = DateTime.Now;
        SelectedAquarium.Measurements.Insert(0, NewMeasurement);
        SortMeasurementsDescending(SelectedAquarium);
        SelectedMeasurement = NewMeasurement;
        NewMeasurement = new WaterParameters();
        OnPropertyChanged(nameof(NewMeasurement));
        RefreshSelectedAquarium();
        RebuildHealthDashboard();
    }

    public bool DuplicateSelectedMeasurement()
    {
        if (SelectedMeasurement is null)
        {
            return false;
        }

        var source = SelectedMeasurement;
        var duplicate = new WaterParameters
        {
            MeasuredAt = DateTime.Now,
            AmmoniaMgPerLiter = source.AmmoniaMgPerLiter,
            NitritesMgPerLiter = source.NitritesMgPerLiter,
            NitratesMgPerLiter = source.NitratesMgPerLiter,
            Ph = source.Ph,
            Gh = source.Gh,
            Kh = source.Kh,
            TemperatureCelsius = source.TemperatureCelsius,
            Notes = source.Notes
        };

        SelectedAquarium.Measurements.Insert(0, duplicate);
        SortMeasurementsDescending(SelectedAquarium);
        SelectedMeasurement = duplicate;
        RefreshSelectedAquarium();
        RebuildHealthDashboard();
        return true;
    }

    public void RefreshMeasurementsAfterEdit()
    {
        var selected = SelectedMeasurement;
        SortMeasurementsDescending(SelectedAquarium);
        SelectedMeasurement = selected;
        RefreshSelectedAquarium();
        RebuildHealthDashboard();
    }

    private static void SortMeasurementsDescending(Aquarium aquarium)
    {
        var ordered = aquarium.Measurements
            .OrderByDescending(measurement => measurement.MeasuredAt)
            .ToList();

        if (ordered.SequenceEqual(aquarium.Measurements))
        {
            return;
        }

        aquarium.Measurements.Clear();
        foreach (var measurement in ordered)
        {
            aquarium.Measurements.Add(measurement);
        }
    }

    public void DeleteSelectedMeasurement()
    {
        if (SelectedMeasurement is null)
        {
            return;
        }

        SelectedAquarium.Measurements.Remove(SelectedMeasurement);
        SelectedMeasurement = null;
        RefreshSelectedAquarium();
        RebuildHealthDashboard();
    }

    public void AddPlant()
    {
        NewPlant.AddedOn = NormalizeDateOrToday(NewPlant.AddedOn);
        NewPlant.Quantity = NormalizeInventoryQuantity(NewPlant.Quantity);
        SelectedAquarium.Plants.Add(NewPlant);
        SortPlantMovementsAscending(SelectedAquarium);
        SelectedPlant = NewPlant;
        NewPlant = new AquariumPlant();
        SelectedPlantReferenceForNewPlant = null;
        OnPropertyChanged(nameof(NewPlant));
        RefreshSelectedAquarium();
    }

    private void ApplyPlantReferenceToNewPlant(PlantReferenceItem reference)
    {
        NewPlant.CommonName = reference.LocalizedCommonName;
        NewPlant.ScientificName = reference.ScientificName;
        NewPlant.GrowthSpeed = ResolvePlantGrowthSpeed(reference.GrowthSpeed);
        NewPlant.LightNeed = ResolvePlantLightNeed(reference.LightNeed);
        OnPropertyChanged(nameof(NewPlant));
    }

    private PlantGrowthSpeed ResolvePlantGrowthSpeed(string growthSpeed)
    {
        var normalized = NormalizeChoiceText(growthSpeed);
        if (normalized.Contains("rapide", StringComparison.Ordinal)
            || normalized.Contains("fast", StringComparison.Ordinal)
            || normalized.Contains("schnell", StringComparison.Ordinal))
        {
            return PlantGrowthSpeed.Fast;
        }

        if (normalized.Contains("lente", StringComparison.Ordinal)
            || normalized.Contains("lent", StringComparison.Ordinal)
            || normalized.Contains("slow", StringComparison.Ordinal)
            || normalized.Contains("langsam", StringComparison.Ordinal))
        {
            return PlantGrowthSpeed.Slow;
        }

        return PlantGrowthSpeed.Medium;
    }

    private string ResolvePlantLightNeed(string lightNeed)
    {
        var normalized = NormalizeChoiceText(lightNeed);
        if (normalized.Contains("forte", StringComparison.Ordinal)
            || normalized.Contains("high", StringComparison.Ordinal)
            || normalized.Contains("stark", StringComparison.Ordinal))
        {
            return text("UiLightHigh");
        }

        if (normalized.Contains("faible", StringComparison.Ordinal)
            || normalized.Contains("low", StringComparison.Ordinal)
            || normalized.Contains("niedrig", StringComparison.Ordinal))
        {
            return text("UiLightLow");
        }

        return text("UiLightMedium");
    }

    private static string NormalizeChoiceText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static DateTime NormalizeDateOrToday(DateTime value)
    {
        return value == default ? DateTime.Today : value.Date;
    }

    public void DeleteSelectedPlant()
    {
        if (SelectedPlant is null)
        {
            return;
        }

        SelectedAquarium.Plants.Remove(SelectedPlant);
        SelectedPlant = null;
        RefreshSelectedAquarium();
    }

    public void AddPopulation()
    {
        NewPopulation.AddedOn = NormalizeDateOrToday(NewPopulation.AddedOn);
        NewPopulation.Quantity = NormalizeInventoryQuantity(NewPopulation.Quantity);
        SelectedAquarium.Population.Add(NewPopulation);
        SortPopulationMovementsAscending(SelectedAquarium);
        SelectedPopulation = NewPopulation;
        NewPopulation = new PopulationMember();
        SelectedAnimalReferenceForNewPopulation = null;
        OnPropertyChanged(nameof(NewPopulation));
        OnPropertyChanged(nameof(NewPopulationType));
        RebuildAnimalReferenceChoices();
        RefreshSelectedAquarium();
    }

    private void ApplyAnimalReferenceToNewPopulation(AnimalReferenceItem reference)
    {
        NewPopulation.CommonName = reference.LocalizedCommonName;
        NewPopulation.SpeciesName = reference.ScientificName;
        NewPopulation.Type = ResolvePopulationType(reference);
        OnPropertyChanged(nameof(NewPopulation));
        OnPropertyChanged(nameof(NewPopulationType));
        RebuildAnimalReferenceChoices();
    }

    private static PopulationType ResolvePopulationType(AnimalReferenceItem reference)
    {
        if (reference.Group == AnimalReferenceGroup.Shrimp)
        {
            return PopulationType.Shrimp;
        }

        if (reference.Group == AnimalReferenceGroup.Snail)
        {
            return PopulationType.Snail;
        }

        if (reference.Group == AnimalReferenceGroup.Other)
        {
            return PopulationType.Other;
        }

        var searchText = NormalizeChoiceText(string.Join(
            ' ',
            reference.LocalizedCommonName,
            reference.CommonName,
            reference.CommonNameFr,
            reference.CommonNameEn,
            reference.CommonNameDe,
            reference.ScientificName,
            reference.Behavior,
            reference.Compatibility));

        if (searchText.Contains("crevette", StringComparison.Ordinal)
            || searchText.Contains("shrimp", StringComparison.Ordinal)
            || searchText.Contains("garnele", StringComparison.Ordinal)
            || searchText.Contains("caridina", StringComparison.Ordinal)
            || searchText.Contains("neocaridina", StringComparison.Ordinal)
            || searchText.Contains("lysmata", StringComparison.Ordinal))
        {
            return PopulationType.Shrimp;
        }

        if (searchText.Contains("escargot", StringComparison.Ordinal)
            || searchText.Contains("mollusque", StringComparison.Ordinal)
            || searchText.Contains("mollusc", StringComparison.Ordinal)
            || searchText.Contains("mollusk", StringComparison.Ordinal)
            || searchText.Contains("snail", StringComparison.Ordinal)
            || searchText.Contains("schnecke", StringComparison.Ordinal)
            || searchText.Contains("mollusken", StringComparison.Ordinal)
            || searchText.Contains("bivalve", StringComparison.Ordinal)
            || searchText.Contains("clam", StringComparison.Ordinal)
            || searchText.Contains("oyster", StringComparison.Ordinal)
            || searchText.Contains("scallop", StringComparison.Ordinal)
            || searchText.Contains("conch", StringComparison.Ordinal)
            || searchText.Contains("cowrie", StringComparison.Ordinal)
            || searchText.Contains("neritina", StringComparison.Ordinal)
            || searchText.Contains("nerite", StringComparison.Ordinal)
            || searchText.Contains("pomacea", StringComparison.Ordinal)
            || searchText.Contains("planorbe", StringComparison.Ordinal))
        {
            return PopulationType.Snail;
        }

        return PopulationType.Fish;
    }

    private static bool MatchesPopulationTypeForNewPopulation(AnimalReferenceItem reference, PopulationType populationType)
    {
        return reference.Group == MapPopulationTypeToAnimalReferenceGroup(populationType);
    }

    private static AnimalReferenceGroup MapPopulationTypeToAnimalReferenceGroup(PopulationType populationType)
    {
        return populationType switch
        {
            PopulationType.Shrimp => AnimalReferenceGroup.Shrimp,
            PopulationType.Snail => AnimalReferenceGroup.Snail,
            PopulationType.Other => AnimalReferenceGroup.Other,
            _ => AnimalReferenceGroup.Fish
        };
    }

    public void DeleteSelectedPopulation()
    {
        if (SelectedPopulation is null)
        {
            return;
        }

        SelectedAquarium.Population.Remove(SelectedPopulation);
        SelectedPopulation = null;
        RefreshSelectedAquarium();
    }

    public bool TryAddIntervention()
    {
        if (!TryParseInterventionTime(out var time))
        {
            return false;
        }

        NewIntervention.OccurredAt = NewIntervention.OccurredAt.Date.Add(time);
        SelectedAquarium.Interventions.Insert(0, NewIntervention);
        SortInterventionsDescending(SelectedAquarium);
        SelectedIntervention = NewIntervention;
        ResetNewIntervention();
        RefreshSelectedAquarium();
        return true;
    }

    private bool TryParseInterventionTime(out TimeSpan time)
    {
        var value = NewInterventionTimeText.Trim();
        if (TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out time)
            || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time))
        {
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        return false;
    }

    private void ResetNewIntervention()
    {
        NewIntervention = new AquariumIntervention();
        NewInterventionTimeText = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(NewIntervention));
        OnPropertyChanged(nameof(NewInterventionTimeText));
    }

    public void DeleteSelectedIntervention()
    {
        if (SelectedIntervention is null)
        {
            return;
        }

        SelectedAquarium.Interventions.Remove(SelectedIntervention);
        SelectedIntervention = null;
        RefreshSelectedAquarium();
    }

    public void RefreshInterventionsAfterEdit()
    {
        var selected = SelectedIntervention;
        SortInterventionsDescending(SelectedAquarium);
        SelectedIntervention = selected;
        RefreshSelectedAquarium();
    }

    private static void SortInterventionsDescending(Aquarium aquarium)
    {
        var ordered = aquarium.Interventions
            .OrderByDescending(intervention => intervention.OccurredAt)
            .ToList();

        if (ordered.SequenceEqual(aquarium.Interventions))
        {
            return;
        }

        aquarium.Interventions.Clear();
        foreach (var intervention in ordered)
        {
            aquarium.Interventions.Add(intervention);
        }
    }

    public void RefreshPlantInventoryAfterEdit()
    {
        var selected = SelectedPlant;
        NormalizePlantEntries(SelectedAquarium);
        SortPlantMovementsAscending(SelectedAquarium);
        SelectedPlant = selected;
        RebuildInventoryTotals();
        RefreshSelectedAquarium();
    }

    public void RefreshPopulationInventoryAfterEdit()
    {
        var selected = SelectedPopulation;
        NormalizePopulationEntries(SelectedAquarium);
        SortPopulationMovementsAscending(SelectedAquarium);
        SelectedPopulation = selected;
        RebuildInventoryTotals();
        RefreshSelectedAquarium();
    }

    private void RebuildInventoryTotals()
    {
        PlantInventoryTotals.Clear();
        PopulationInventoryTotals.Clear();

        if (selectedAquarium is null)
        {
            return;
        }

        foreach (var total in selectedAquarium.Plants
            .GroupBy(plant => BuildSpeciesKey(plant.CommonName, plant.ScientificName), StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new PlantInventoryTotal(
                    first.CommonName,
                    first.ScientificName,
                    group.Sum(plant => ToSignedQuantity(plant.Quantity, plant.MovementType)));
            })
            .Where(total => total.Quantity != 0)
            .OrderBy(total => total.CommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(total => total.ScientificName, StringComparer.CurrentCultureIgnoreCase))
        {
            PlantInventoryTotals.Add(total);
        }

        foreach (var total in selectedAquarium.Population
            .GroupBy(member => new PopulationInventoryKey(member.Type, BuildSpeciesKey(member.CommonName, member.SpeciesName)))
            .Select(group =>
            {
                var first = group.First();
                return new PopulationInventoryTotal(
                    BuildPopulationFamilyLabel(first.Type),
                    first.CommonName,
                    first.SpeciesName,
                    group.Sum(member => ToSignedQuantity(member.Quantity, member.MovementType)));
            })
            .Where(total => total.Quantity != 0)
            .OrderBy(total => total.Family, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(total => total.CommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(total => total.SpeciesName, StringComparer.CurrentCultureIgnoreCase))
        {
            PopulationInventoryTotals.Add(total);
        }
    }

    private string BuildPopulationFamilyLabel(PopulationType type)
    {
        return type switch
        {
            PopulationType.Shrimp => text("UiPopulationTypeShrimp"),
            PopulationType.Snail => text("UiPopulationTypeSnail"),
            PopulationType.Other => text("UiPopulationTypeOther"),
            _ => text("UiPopulationTypeFish")
        };
    }

    private static string BuildSpeciesKey(string commonName, string scientificName)
    {
        var species = string.IsNullOrWhiteSpace(scientificName)
            ? commonName
            : scientificName;

        return NormalizeChoiceText(species);
    }

    private static int ToSignedQuantity(int quantity, InventoryMovementType movementType)
    {
        var normalized = NormalizeInventoryQuantity(quantity);
        return movementType == InventoryMovementType.Removal
            ? -normalized
            : normalized;
    }

    private static int NormalizeInventoryQuantity(int quantity)
    {
        return Math.Max(1, Math.Abs(quantity));
    }

    private static void NormalizeInventoryEntries(Aquarium aquarium)
    {
        NormalizePlantEntries(aquarium);
        NormalizePopulationEntries(aquarium);
    }

    private static void SortInventoryMovementsAscending(Aquarium aquarium)
    {
        SortPlantMovementsAscending(aquarium);
        SortPopulationMovementsAscending(aquarium);
    }

    private static void SortPlantMovementsAscending(Aquarium aquarium)
    {
        var ordered = aquarium.Plants
            .OrderBy(plant => plant.AddedOn)
            .ThenBy(plant => plant.CommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(plant => plant.ScientificName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ordered.SequenceEqual(aquarium.Plants))
        {
            return;
        }

        aquarium.Plants.Clear();
        foreach (var plant in ordered)
        {
            aquarium.Plants.Add(plant);
        }
    }

    private static void SortPopulationMovementsAscending(Aquarium aquarium)
    {
        var ordered = aquarium.Population
            .OrderBy(member => member.AddedOn)
            .ThenBy(member => member.CommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.SpeciesName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ordered.SequenceEqual(aquarium.Population))
        {
            return;
        }

        aquarium.Population.Clear();
        foreach (var member in ordered)
        {
            aquarium.Population.Add(member);
        }
    }

    private static void NormalizePlantEntries(Aquarium aquarium)
    {
        foreach (var plant in aquarium.Plants)
        {
            plant.AddedOn = NormalizeDateOrToday(plant.AddedOn);
            plant.Quantity = NormalizeInventoryQuantity(plant.Quantity);
        }
    }

    private static void NormalizePopulationEntries(Aquarium aquarium)
    {
        foreach (var member in aquarium.Population)
        {
            member.AddedOn = NormalizeDateOrToday(member.AddedOn);
            member.Quantity = NormalizeInventoryQuantity(member.Quantity);
        }
    }

    private void RefreshSelectedAquarium()
    {
        OnPropertyChanged(nameof(SelectedAquarium));
        RebuildInventoryTotals();
        RebuildHealthDashboard();
        RebuildPlantReference();
        RebuildAnimalReference();
        RefreshGridViews();
    }

    public void ClearGridFilters()
    {
        MeasurementSearchText = string.Empty;
        MeasurementPeriodFilterCode = FilterAllCode;
        PlantSearchText = string.Empty;
        PlantMovementFilterCode = FilterAllCode;
        PopulationSearchText = string.Empty;
        PopulationMovementFilterCode = FilterAllCode;
        PopulationTypeFilterCode = FilterAllCode;
        InterventionSearchText = string.Empty;
        InterventionTypeFilterCode = FilterAllCode;
        RefreshGridViews();
    }

    private void RebuildGridViews()
    {
        if (selectedAquarium is null)
        {
            MeasurementsView = null;
            PlantsView = null;
            PopulationView = null;
            InterventionsView = null;
            RefreshGridResultStates();
            return;
        }

        MeasurementsView = CollectionViewSource.GetDefaultView(selectedAquarium.Measurements);
        MeasurementsView.Filter = MatchesMeasurementGridFilters;
        ApplySingleSort(MeasurementsView, nameof(WaterParameters.MeasuredAt), ListSortDirection.Descending);

        PlantsView = CollectionViewSource.GetDefaultView(selectedAquarium.Plants);
        PlantsView.Filter = MatchesPlantGridFilters;
        ApplySingleSort(PlantsView, nameof(AquariumPlant.AddedOn), ListSortDirection.Ascending);

        PopulationView = CollectionViewSource.GetDefaultView(selectedAquarium.Population);
        PopulationView.Filter = MatchesPopulationGridFilters;
        ApplySingleSort(PopulationView, nameof(PopulationMember.AddedOn), ListSortDirection.Ascending);

        InterventionsView = CollectionViewSource.GetDefaultView(selectedAquarium.Interventions);
        InterventionsView.Filter = MatchesInterventionGridFilters;
        ApplySingleSort(InterventionsView, nameof(AquariumIntervention.OccurredAt), ListSortDirection.Descending);

        RefreshGridViews();
    }

    private static void ApplySingleSort(ICollectionView view, string propertyName, ListSortDirection direction)
    {
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(propertyName, direction));
    }

    private void RefreshGridViews()
    {
        if (TryRefreshGridViews())
        {
            RefreshGridResultStates();
            return;
        }

        QueueGridViewsRefresh();
    }

    private bool TryRefreshGridViews()
    {
        return TryRefreshView(MeasurementsView)
            & TryRefreshView(PlantsView)
            & TryRefreshView(PopulationView)
            & TryRefreshView(InterventionsView);
    }

    private static bool TryRefreshView(ICollectionView? view)
    {
        if (view is null)
        {
            return true;
        }

        if (view is IEditableCollectionView editableView
            && (editableView.IsAddingNew || editableView.IsEditingItem))
        {
            return false;
        }

        try
        {
            view.Refresh();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void QueueGridViewsRefresh()
    {
        if (isGridRefreshQueued)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            RefreshGridResultStates();
            return;
        }

        isGridRefreshQueued = true;
        _ = dispatcher.BeginInvoke(
            new Action(() =>
            {
                isGridRefreshQueued = false;
                TryRefreshGridViews();
                RefreshGridResultStates();
            }),
            DispatcherPriority.ContextIdle);
    }

    private void RefreshGridResultStates()
    {
        OnPropertyChanged(nameof(HasNoMeasurementResults));
        OnPropertyChanged(nameof(HasNoPlantResults));
        OnPropertyChanged(nameof(HasNoPopulationResults));
        OnPropertyChanged(nameof(HasNoInterventionResults));
    }

    private bool MatchesMeasurementGridFilters(object candidate)
    {
        if (candidate is not WaterParameters measurement)
        {
            return false;
        }

        if (measurementPeriodFilterCode is MeasurementPeriod30Code or MeasurementPeriod90Code
            && int.TryParse(measurementPeriodFilterCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            && selectedAquarium.Measurements.Count > 0)
        {
            var latestDate = selectedAquarium.Measurements.Max(item => item.MeasuredAt);
            var lowerBound = latestDate.AddDays(-(days - 1));
            if (measurement.MeasuredAt < lowerBound)
            {
                return false;
            }
        }

        return MatchesSearch(
            measurementSearchText,
            measurement.MeasuredAt.ToString("g", CultureInfo.CurrentCulture),
            measurement.AmmoniaMgPerLiter?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.NitritesMgPerLiter?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.NitratesMgPerLiter?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.Ph?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.Gh?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.Kh?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.TemperatureCelsius?.ToString("0.####", CultureInfo.CurrentCulture),
            measurement.Notes);
    }

    private bool MatchesPlantGridFilters(object candidate)
    {
        if (candidate is not AquariumPlant plant)
        {
            return false;
        }

        if (!MatchesMovementFilter(plant.MovementType, plantMovementFilterCode))
        {
            return false;
        }

        return MatchesSearch(
            plantSearchText,
            plant.AddedOn.ToString("d", CultureInfo.CurrentCulture),
            BuildMovementLabel(plant.MovementType),
            plant.Quantity.ToString(CultureInfo.CurrentCulture),
            plant.CommonName,
            plant.ScientificName,
            BuildPlantGrowthLabel(plant.GrowthSpeed),
            plant.LightNeed,
            plant.Notes);
    }

    private bool MatchesPopulationGridFilters(object candidate)
    {
        if (candidate is not PopulationMember member)
        {
            return false;
        }

        if (!MatchesMovementFilter(member.MovementType, populationMovementFilterCode))
        {
            return false;
        }

        if (populationTypeFilterCode != FilterAllCode
            && (!Enum.TryParse<PopulationType>(populationTypeFilterCode, out var type) || member.Type != type))
        {
            return false;
        }

        return MatchesSearch(
            populationSearchText,
            member.AddedOn.ToString("d", CultureInfo.CurrentCulture),
            BuildMovementLabel(member.MovementType),
            BuildPopulationFamilyLabel(member.Type),
            member.Quantity.ToString(CultureInfo.CurrentCulture),
            member.CommonName,
            member.SpeciesName,
            member.Notes);
    }

    private bool MatchesInterventionGridFilters(object candidate)
    {
        if (candidate is not AquariumIntervention intervention)
        {
            return false;
        }

        if (interventionTypeFilterCode != FilterAllCode
            && (!Enum.TryParse<InterventionType>(interventionTypeFilterCode, out var type) || intervention.Type != type))
        {
            return false;
        }

        return MatchesSearch(
            interventionSearchText,
            intervention.OccurredAt.ToString("g", CultureInfo.CurrentCulture),
            BuildInterventionTypeLabel(intervention.Type),
            intervention.ProductName,
            intervention.ProductQuantity,
            intervention.WaterVolumeLiters?.ToString("0.####", CultureInfo.CurrentCulture),
            intervention.WaterPercentage?.ToString("0.####", CultureInfo.CurrentCulture),
            intervention.PopulationChangeReason,
            intervention.PopulationChangeCount?.ToString(CultureInfo.CurrentCulture),
            intervention.Notes);
    }

    private static bool MatchesMovementFilter(InventoryMovementType movementType, string filterCode)
    {
        return filterCode == FilterAllCode
            || string.Equals(movementType.ToString(), filterCode, StringComparison.Ordinal);
    }

    private string BuildMovementLabel(InventoryMovementType type)
    {
        return type == InventoryMovementType.Removal
            ? text("UiMovementRemoval")
            : text("UiMovementAddition");
    }

    private string BuildPlantGrowthLabel(PlantGrowthSpeed growthSpeed)
    {
        return growthSpeed switch
        {
            PlantGrowthSpeed.Slow => text("UiGrowthSlow"),
            PlantGrowthSpeed.Fast => text("UiGrowthFast"),
            _ => text("UiGrowthMedium")
        };
    }

    private string BuildInterventionTypeLabel(InterventionType type)
    {
        return type switch
        {
            InterventionType.Fertilization => text("UiInterventionFertilization"),
            InterventionType.FilterCleaning => text("UiInterventionFilterCleaning"),
            InterventionType.PopulationAdded => text("UiInterventionPopulationAdded"),
            InterventionType.PopulationRemoved => text("UiInterventionPopulationRemoved"),
            InterventionType.MedicalTreatment => text("UiInterventionMedicalTreatment"),
            InterventionType.Other => text("UiInterventionOther"),
            _ => text("UiInterventionWaterChange")
        };
    }

    private static bool MatchesSearch(string searchText, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var normalizedSearch = NormalizeChoiceText(searchText);
        return values.Any(value => !string.IsNullOrWhiteSpace(value)
            && NormalizeChoiceText(value).Contains(normalizedSearch, StringComparison.Ordinal));
    }

    private static string NormalizeMovementFilterCode(string? value)
    {
        return value is nameof(InventoryMovementType.Addition) or nameof(InventoryMovementType.Removal)
            ? value
            : FilterAllCode;
    }

    private static string NormalizePopulationTypeFilterCode(string? value)
    {
        return Enum.TryParse<PopulationType>(value, out var type)
            ? type.ToString()
            : FilterAllCode;
    }

    private static string NormalizeAnimalReferenceGroupFilterCode(string? value)
    {
        return Enum.TryParse<AnimalReferenceGroup>(value, out var group)
            ? group.ToString()
            : FilterAllCode;
    }

    private static bool MatchesAnimalReferenceGroupFilter(AnimalReferenceGroup group, string filterCode)
    {
        return filterCode == FilterAllCode
            || (Enum.TryParse<AnimalReferenceGroup>(filterCode, out var filterGroup) && group == filterGroup);
    }

    private static string NormalizeInterventionTypeFilterCode(string? value)
    {
        return Enum.TryParse<InterventionType>(value, out var type)
            ? type.ToString()
            : FilterAllCode;
    }

    private Aquarium CreateDefaultAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = text("DefaultMainAquarium"),
            VolumeLiters = 120,
            ContainerType = ContainerTypeAquarium,
            WaterType = WaterTypeFreshwaterTropical,
            Notes = text("DefaultMainAquariumNote")
        };

        aquarium.Measurements.Add(new WaterParameters
        {
            AmmoniaMgPerLiter = 0,
            NitritesMgPerLiter = 0,
            NitratesMgPerLiter = 10,
            Ph = 7.2m,
            Gh = 8,
            Kh = 5,
            TemperatureCelsius = 24.5m
        });
        aquarium.Plants.Add(new AquariumPlant { CommonName = "Anubias", ScientificName = "Anubias barteri", LightNeed = text("DefaultPlantLight") });
        aquarium.Population.Add(new PopulationMember { CommonName = text("DefaultNeonName"), SpeciesName = "Paracheirodon innesi", Quantity = 10 });
        return aquarium;
    }

    private void RebuildHealthDashboard()
    {
        HealthIndicators.Clear();
        HealthTrendSeries.Clear();
        HealthRecommendedActions.Clear();

        var aquarium = selectedAquarium;
        if (aquarium is null)
        {
            HealthLastMeasurementAt = "-";
            HealthGlobalStatus = text("HealthStatusNoData");
            HealthRecommendedActions.Add(text("HealthActionNoData"));
            return;
        }

        if (aquarium.Measurements.Count == 0)
        {
            HealthLastMeasurementAt = "-";
            HealthGlobalStatus = text("HealthStatusNoData");
            HealthRecommendedActions.Add(text("HealthActionNoData"));
            return;
        }

        var ordered = aquarium.Measurements.OrderByDescending(m => m.MeasuredAt).ToList();
        var latest = ordered[0];
        var previous = ordered.Count > 1 ? ordered[1] : null;
        var healthRules = GetHealthRulesForAquarium(aquarium);
        HealthLastMeasurementAt = latest.MeasuredAt.ToString("g", CultureInfo.CurrentCulture);

        var hasCritical = false;
        var hasWarning = false;
        foreach (var rule in healthRules)
        {
            var latestValue = rule.Selector(latest);
            var previousValue = previous is null ? null : rule.Selector(previous);
            var trend = BuildTrend(latestValue, previousValue);
            var alert = BuildAlert(latestValue, rule);

            hasCritical |= alert == text("HealthAlertCritical");
            hasWarning |= alert == text("HealthAlertWarning");

            HealthIndicators.Add(new HealthIndicator(
                text(rule.ParameterKey),
                latestValue?.ToString("0.##") ?? "-",
                BuildTargetRange(rule),
                trend,
                alert));
        }

        RebuildHealthTrendSeries(aquarium);

        if (hasCritical)
        {
            HealthGlobalStatus = text("HealthStatusCritical");
            AddActionsForCritical(latest, aquarium);
            return;
        }

        if (hasWarning)
        {
            HealthGlobalStatus = text("HealthStatusWarning");
            AddActionsForWarning(latest, aquarium);
            return;
        }

        HealthGlobalStatus = text("HealthStatusOk");
        HealthRecommendedActions.Add(text("HealthActionOk"));
    }

    private void RebuildHealthTrendSeries(Aquarium aquarium)
    {
        var filtered = GetTrendMeasurements(aquarium);
        if (filtered.Count < 2)
        {
            return;
        }

        AddTrendSeries(ParameterAmmonia, "mg/L", filtered, m => m.AmmoniaMgPerLiter);
        AddTrendSeries(ParameterNitrites, "mg/L", filtered, m => m.NitritesMgPerLiter);
        AddTrendSeries(ParameterNitrates, "mg/L", filtered, m => m.NitratesMgPerLiter);
        AddTrendSeries(ParameterPh, string.Empty, filtered, m => m.Ph);
        AddTrendSeries(ParameterGh, string.Empty, filtered, m => m.Gh);
        AddTrendSeries(ParameterKh, string.Empty, filtered, m => m.Kh);
        AddTrendSeries(ParameterTemperature, "C", filtered, m => m.TemperatureCelsius);
    }

    private List<WaterParameters> GetTrendMeasurements(Aquarium aquarium)
    {
        var ordered = aquarium.Measurements
            .OrderBy(m => m.MeasuredAt)
            .ToList();

        if (ordered.Count == 0)
        {
            return ordered;
        }

        var days = NormalizeTrendPeriod(SelectedTrendPeriodDays);
        if (days <= 0)
        {
            return ordered;
        }

        var limit = ordered[^1].MeasuredAt.AddDays(-(days - 1));
        return ordered
            .Where(m => m.MeasuredAt >= limit)
            .ToList();
    }

    private void AddTrendSeries(string parameterKey, string unit, IReadOnlyList<WaterParameters> measurements, Func<WaterParameters, decimal?> selector)
    {
        if (!TrendParameterOptions.Any(option => option.IsSelected && string.Equals(option.ParameterKey, parameterKey, StringComparison.Ordinal)))
        {
            return;
        }

        var values = measurements
            .Select(selector)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (values.Count < 2)
        {
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        if (range <= 0m)
        {
            range = 1m;
        }

        const double plotLeft = 24d;
        const double plotTop = 8d;
        const double plotRight = 242d;
        const double plotBottom = 132d;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1
                ? plotLeft
                : plotLeft + i * plotWidth / (values.Count - 1d);
            var normalized = (double)((values[i] - min) / range);
            var y = plotBottom - normalized * plotHeight;
            points.Add(new Point(x, y));
        }

        var latestValue = values[^1];
        var latestLabel = string.IsNullOrWhiteSpace(unit)
            ? latestValue.ToString("0.##")
            : $"{latestValue:0.##} {unit}";

        var mid = (min + max) / 2m;
        var yAxisDecimals = DetermineYAxisDecimals(min, mid, max);
        var yTopLabel = FormatAxisValue(max, unit, yAxisDecimals);
        var yMidLabel = FormatAxisValue(mid, unit, yAxisDecimals);
        var yBottomLabel = FormatAxisValue(min, unit, yAxisDecimals);
        var xStartLabel = measurements[0].MeasuredAt.ToString("d", CultureInfo.CurrentCulture);
        var xMidLabel = measurements[measurements.Count / 2].MeasuredAt.ToString("d", CultureInfo.CurrentCulture);
        var xEndLabel = measurements[^1].MeasuredAt.ToString("d", CultureInfo.CurrentCulture);

        HealthTrendSeries.Add(new HealthTrendSeries(text(parameterKey), latestLabel, points, yTopLabel, yMidLabel, yBottomLabel, xStartLabel, xMidLabel, xEndLabel));
    }

    private static int DetermineYAxisDecimals(decimal min, decimal mid, decimal max)
    {
        if (min == max)
        {
            return 2;
        }

        for (var decimals = 2; decimals <= 4; decimals++)
        {
            var roundedMin = Math.Round(min, decimals);
            var roundedMid = Math.Round(mid, decimals);
            var roundedMax = Math.Round(max, decimals);
            var midIsDistinct = roundedMid != roundedMin && roundedMid != roundedMax;
            if (midIsDistinct)
            {
                return decimals;
            }
        }

        return 4;
    }

    private static string FormatAxisValue(decimal value, string unit, int decimals)
    {
        var format = decimals switch
        {
            <= 0 => "0",
            _ => $"0.{new string('#', decimals)}"
        };

        return string.IsNullOrWhiteSpace(unit)
            ? value.ToString(format)
            : $"{value.ToString(format)} {unit}";
    }

    private static int NormalizeTrendPeriod(int value)
    {
        return value switch
        {
            7 => 7,
            30 => 30,
            90 => 90,
            0 => 0,
            _ => 30
        };
    }

    private void RebuildPlantReference()
    {
        PlantReferencesFiltered.Clear();
        RebuildPlantReferenceChoices();
        if (selectedAquarium is null)
        {
            PlantReferenceEnvironmentLabel = text("UiPlantReferenceLabelUnknown");
            return;
        }

        var environment = ResolveEnvironmentType(selectedAquarium.WaterType);
        PlantReferenceEnvironmentLabel = BuildPlantReferenceEnvironmentLabel(selectedAquarium, environment);

        foreach (var item in plantReferenceCatalog
            .Where(item => item.Environment == environment)
            .Where(MatchesPlantFilters)
            .OrderBy(item => item.LocalizedCommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ScientificName, StringComparer.CurrentCultureIgnoreCase))
        {
            PlantReferencesFiltered.Add(item);
        }
    }

    private void RebuildPlantReferenceChoices()
    {
        PlantReferenceChoices.Clear();
        if (selectedAquarium is null)
        {
            SelectedPlantReferenceForNewPlant = null;
            return;
        }

        var environment = ResolveEnvironmentType(selectedAquarium.WaterType);
        foreach (var item in plantReferenceCatalog
            .Where(item => item.Environment == environment)
            .OrderBy(item => item.LocalizedCommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ScientificName, StringComparer.CurrentCultureIgnoreCase))
        {
            PlantReferenceChoices.Add(item);
        }

        if (SelectedPlantReferenceForNewPlant is not null && !PlantReferenceChoices.Contains(SelectedPlantReferenceForNewPlant))
        {
            SelectedPlantReferenceForNewPlant = null;
        }
    }

    public void ApplyPlantReferenceFilters()
    {
        RebuildPlantReference();
    }

    public void ResetPlantReferenceFilters()
    {
        PlantFilterPhMin = string.Empty;
        PlantFilterPhMax = string.Empty;
        PlantFilterGhMin = string.Empty;
        PlantFilterGhMax = string.Empty;
        PlantFilterKhMin = string.Empty;
        PlantFilterKhMax = string.Empty;
        PlantFilterTempMin = string.Empty;
        PlantFilterTempMax = string.Empty;
        PlantFilterAmmoniaMax = string.Empty;
        PlantFilterNitritesMax = string.Empty;
        PlantFilterNitratesMax = string.Empty;
        RebuildPlantReference();
    }

    private void RebuildAnimalReference()
    {
        AnimalReferencesFiltered.Clear();
        RebuildAnimalReferenceChoices();
        if (selectedAquarium is null)
        {
            AnimalReferenceEnvironmentLabel = text("UiAnimalReferenceLabelUnknown");
            return;
        }

        var environment = ResolveAnimalEnvironmentType(selectedAquarium.WaterType);
        AnimalReferenceEnvironmentLabel = BuildAnimalReferenceEnvironmentLabel(selectedAquarium, environment);

        foreach (var item in animalReferenceCatalog
            .Where(item => item.Environment == environment)
            .Where(MatchesAnimalFilters)
            .OrderBy(item => item.LocalizedCommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ScientificName, StringComparer.CurrentCultureIgnoreCase))
        {
            AnimalReferencesFiltered.Add(item);
        }
    }

    private void RebuildAnimalReferenceChoices()
    {
        AnimalReferenceChoices.Clear();
        if (selectedAquarium is null)
        {
            SelectedAnimalReferenceForNewPopulation = null;
            return;
        }

        var environment = ResolveAnimalEnvironmentType(selectedAquarium.WaterType);
        var populationType = NewPopulation.Type;
        foreach (var item in animalReferenceCatalog
            .Where(item => item.Environment == environment)
            .Where(item => MatchesPopulationTypeForNewPopulation(item, populationType))
            .OrderBy(item => item.LocalizedCommonName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ScientificName, StringComparer.CurrentCultureIgnoreCase))
        {
            AnimalReferenceChoices.Add(item);
        }

        if (SelectedAnimalReferenceForNewPopulation is not null && !AnimalReferenceChoices.Contains(SelectedAnimalReferenceForNewPopulation))
        {
            SelectedAnimalReferenceForNewPopulation = null;
        }
    }

    public void ApplyAnimalReferenceFilters()
    {
        RebuildAnimalReference();
    }

    public void ResetAnimalReferenceFilters()
    {
        AnimalFilterPhMin = string.Empty;
        AnimalFilterPhMax = string.Empty;
        AnimalFilterGhMin = string.Empty;
        AnimalFilterGhMax = string.Empty;
        AnimalFilterKhMin = string.Empty;
        AnimalFilterKhMax = string.Empty;
        AnimalFilterTempMin = string.Empty;
        AnimalFilterTempMax = string.Empty;
        AnimalFilterAmmoniaMax = string.Empty;
        AnimalFilterNitritesMax = string.Empty;
        AnimalFilterNitratesMax = string.Empty;
        AnimalReferenceGroupFilterCode = FilterAllCode;
        RebuildAnimalReference();
    }

    private bool MatchesAnimalFilters(AnimalReferenceItem item)
    {
        return MatchesAnimalReferenceGroupFilter(item.Group, AnimalReferenceGroupFilterCode)
            && MatchesMin(item.PhMin, AnimalFilterPhMin)
            && MatchesMax(item.PhMax, AnimalFilterPhMax)
            && MatchesMin(item.GhMin, AnimalFilterGhMin)
            && MatchesMax(item.GhMax, AnimalFilterGhMax)
            && MatchesMin(item.KhMin, AnimalFilterKhMin)
            && MatchesMax(item.KhMax, AnimalFilterKhMax)
            && MatchesMin(item.TemperatureMin, AnimalFilterTempMin)
            && MatchesMax(item.TemperatureMax, AnimalFilterTempMax)
            && MatchesMax(item.AmmoniaMax, AnimalFilterAmmoniaMax)
            && MatchesMax(item.NitritesMax, AnimalFilterNitritesMax)
            && MatchesMax(item.NitratesMax, AnimalFilterNitratesMax);
    }

    private bool MatchesPlantFilters(PlantReferenceItem item)
    {
        return MatchesMin(item.PhMin, PlantFilterPhMin)
            && MatchesMax(item.PhMax, PlantFilterPhMax)
            && MatchesMin(item.GhMin, PlantFilterGhMin)
            && MatchesMax(item.GhMax, PlantFilterGhMax)
            && MatchesMin(item.KhMin, PlantFilterKhMin)
            && MatchesMax(item.KhMax, PlantFilterKhMax)
            && MatchesMin(item.TemperatureMin, PlantFilterTempMin)
            && MatchesMax(item.TemperatureMax, PlantFilterTempMax)
            && MatchesMax(item.AmmoniaMax, PlantFilterAmmoniaMax)
            && MatchesMax(item.NitritesMax, PlantFilterNitritesMax)
            && MatchesMax(item.NitratesMax, PlantFilterNitratesMax);
    }

    private static bool MatchesMin(decimal? value, string filterText)
    {
        if (!TryParseFilter(filterText, out var filter))
        {
            return true;
        }

        return value.HasValue && value.Value >= filter;
    }

    private static bool MatchesMax(decimal? value, string filterText)
    {
        if (!TryParseFilter(filterText, out var filter))
        {
            return true;
        }

        return value.HasValue && value.Value <= filter;
    }

    private static bool TryParseFilter(string text, out decimal value)
    {
        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private string BuildPlantReferenceEnvironmentLabel(Aquarium aquarium, PlantReferenceEnvironment environment)
    {
        if (IsFishPondContainerType(aquarium.ContainerType))
        {
            return text("UiPlantReferenceLabelPond");
        }

        return environment == PlantReferenceEnvironment.Marine
            ? text("UiPlantReferenceLabelMarine")
            : text("UiPlantReferenceLabelFreshwater");
    }

    private string BuildAnimalReferenceEnvironmentLabel(Aquarium aquarium, AnimalReferenceEnvironment environment)
    {
        if (IsFishPondContainerType(aquarium.ContainerType))
        {
            return text("UiAnimalReferenceLabelPond");
        }

        return environment == AnimalReferenceEnvironment.Marine
            ? text("UiAnimalReferenceLabelMarine")
            : text("UiAnimalReferenceLabelFreshwater");
    }

    private static PlantReferenceEnvironment ResolveEnvironmentType(string? waterType)
    {
        return IsMarineWaterType(waterType)
            ? PlantReferenceEnvironment.Marine
            : PlantReferenceEnvironment.FreshwaterTropical;
    }

    private static AnimalReferenceEnvironment ResolveAnimalEnvironmentType(string? waterType)
    {
        return IsMarineWaterType(waterType)
            ? AnimalReferenceEnvironment.Marine
            : AnimalReferenceEnvironment.FreshwaterTropical;
    }

    private static bool IsMarineWaterType(string? waterType)
    {
        return NormalizeWaterTypeCode(waterType) == WaterTypeMarine;
    }

    private static bool IsFishPondContainerType(string? containerType)
    {
        return NormalizeContainerTypeCode(containerType) == ContainerTypeFishPond;
    }

    private static void NormalizeAquariumClassification(Aquarium aquarium)
    {
        aquarium.ContainerType = NormalizeContainerTypeCode(aquarium.ContainerType);
        aquarium.WaterType = NormalizeWaterTypeCode(aquarium.WaterType);
        if (IsFishPondContainerType(aquarium.ContainerType))
        {
            aquarium.WaterType = WaterTypeFreshwaterTropical;
        }
    }

    private static string NormalizeContainerTypeCode(string? containerType)
    {
        if (string.IsNullOrWhiteSpace(containerType))
        {
            return ContainerTypeAquarium;
        }

        var normalized = containerType.Trim().ToLowerInvariant();
        if (string.Equals(normalized, ContainerTypeFishPond.ToLowerInvariant(), StringComparison.Ordinal)
            || normalized.Contains("bassin", StringComparison.Ordinal)
            || normalized.Contains("pond", StringComparison.Ordinal)
            || normalized.Contains("teich", StringComparison.Ordinal))
        {
            return ContainerTypeFishPond;
        }

        return ContainerTypeAquarium;
    }

    private static string NormalizeWaterTypeCode(string? waterType)
    {
        if (string.IsNullOrWhiteSpace(waterType))
        {
            return WaterTypeFreshwaterTropical;
        }

        var normalized = waterType.Trim().ToLowerInvariant();
        if (string.Equals(normalized, WaterTypeMarine.ToLowerInvariant(), StringComparison.Ordinal)
            || normalized.Contains("mer", StringComparison.Ordinal)
            || normalized.Contains("sea", StringComparison.Ordinal)
            || normalized.Contains("marine", StringComparison.Ordinal)
            || normalized.Contains("meer", StringComparison.Ordinal))
        {
            return WaterTypeMarine;
        }

        return WaterTypeFreshwaterTropical;
    }

    private static bool IsWithin(decimal? value, decimal? min, decimal? max)
    {
        if (!value.HasValue || !min.HasValue || !max.HasValue)
        {
            return true;
        }

        return value.Value >= min.Value && value.Value <= max.Value;
    }

    private void InitializeTrendParameterOptions()
    {
        AddTrendOption(ParameterAmmonia);
        AddTrendOption(ParameterNitrites);
        AddTrendOption(ParameterNitrates);
        AddTrendOption(ParameterPh);
        AddTrendOption(ParameterGh);
        AddTrendOption(ParameterKh);
        AddTrendOption(ParameterTemperature);
    }

    private void AddTrendOption(string parameterKey)
    {
        var option = new TrendParameterOption(parameterKey, text(parameterKey), true);
        option.PropertyChanged += OnTrendParameterOptionChanged;
        TrendParameterOptions.Add(option);
    }

    private void UpdateTrendParameterOptionLabels()
    {
        foreach (var option in TrendParameterOptions)
        {
            option.SetDisplayName(text(option.ParameterKey));
        }
    }

    private void OnTrendParameterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrendParameterOption.IsSelected))
        {
            RebuildHealthDashboard();
        }
    }

    private string BuildTrend(decimal? latest, decimal? previous)
    {
        if (latest is null || previous is null)
        {
            return text("HealthTrendNotAvailable");
        }

        if (latest > previous)
        {
            return text("HealthTrendUp");
        }

        if (latest < previous)
        {
            return text("HealthTrendDown");
        }

        return text("HealthTrendStable");
    }

    private string BuildAlert(decimal? value, HealthRule rule)
    {
        if (value is null)
        {
            return text("HealthAlertNoData");
        }

        if (value < rule.CriticalMin || value > rule.CriticalMax)
        {
            return text("HealthAlertCritical");
        }

        if (value < rule.WarningMin || value > rule.WarningMax)
        {
            return text("HealthAlertWarning");
        }

        return text("HealthAlertOk");
    }

    private static IReadOnlyList<HealthRule> GetHealthRulesForAquarium(Aquarium aquarium)
    {
        if (IsFishPondContainerType(aquarium.ContainerType))
        {
            return PondHealthRules;
        }

        return IsMarineWaterType(aquarium.WaterType)
            ? MarineHealthRules
            : FreshwaterHealthRules;
    }

    private static string BuildTargetRange(HealthRule rule)
    {
        return $"{rule.WarningMin:0.###} - {rule.WarningMax:0.###}";
    }

    private void AddActionsForCritical(WaterParameters latest, Aquarium aquarium)
    {
        var ammoniaCritical = IsMarineWaterType(aquarium.WaterType) ? 0.1m : 0.2m;
        var nitritesCritical = 0.1m;
        if ((latest.AmmoniaMgPerLiter ?? 0m) > ammoniaCritical || (latest.NitritesMgPerLiter ?? 0m) > nitritesCritical)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalWaterChange"));
            HealthRecommendedActions.Add(text("HealthActionCriticalFeeding"));
        }

        var isPond = IsFishPondContainerType(aquarium.ContainerType);
        var minTemperature = isPond ? 4m : IsMarineWaterType(aquarium.WaterType) ? 22m : 18m;
        var maxTemperature = isPond ? 32m : 30m;
        if ((latest.TemperatureCelsius ?? 24m) < minTemperature || (latest.TemperatureCelsius ?? 24m) > maxTemperature)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalTemperature"));
        }

        if (HealthRecommendedActions.Count == 0)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalGeneric"));
        }
    }

    private void AddActionsForWarning(WaterParameters latest, Aquarium aquarium)
    {
        var rules = GetHealthRulesForAquarium(aquarium);
        var nitratesRule = rules.First(rule => rule.ParameterKey == ParameterNitrates);
        var phRule = rules.First(rule => rule.ParameterKey == ParameterPh);
        var ghRule = rules.First(rule => rule.ParameterKey == ParameterGh);
        var khRule = rules.First(rule => rule.ParameterKey == ParameterKh);

        if ((latest.NitratesMgPerLiter ?? 0m) > nitratesRule.WarningMax)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningNitrates"));
        }

        if ((latest.Ph ?? 7m) < phRule.WarningMin || (latest.Ph ?? 7m) > phRule.WarningMax)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningPh"));
        }

        if ((latest.Gh ?? 8m) < ghRule.WarningMin
            || (latest.Gh ?? 8m) > ghRule.WarningMax
            || (latest.Kh ?? 5m) < khRule.WarningMin
            || (latest.Kh ?? 5m) > khRule.WarningMax)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningHardness"));
        }

        if (HealthRecommendedActions.Count == 0)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningGeneric"));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record WaterTypeOption(string Code, string Label);
public sealed record ContainerTypeOption(string Code, string Label);
public sealed record InterventionTypeOption(InterventionType Type, string Label);
public sealed record FilterOption(string Code, string Label);
public sealed record HealthIndicator(string Name, string LatestDisplay, string TargetRange, string Trend, string Alert);
public sealed record PlantInventoryTotal(string CommonName, string ScientificName, int Quantity);
public sealed record PopulationInventoryTotal(string Family, string CommonName, string SpeciesName, int Quantity);
public sealed record PopulationInventoryKey(PopulationType Type, string SpeciesKey);
public sealed record HealthTrendSeries(
    string Name,
    string LatestValueLabel,
    PointCollection Points,
    string YTopLabel,
    string YMidLabel,
    string YBottomLabel,
    string XStartLabel,
    string XMidLabel,
    string XEndLabel);

public sealed class PlantReferenceItem : INotifyPropertyChanged
{
    public PlantReferenceItem(
        Guid id,
        PlantReferenceEnvironment environment,
        string commonName,
        string commonNameFr,
        string commonNameEn,
        string commonNameDe,
        string scientificName,
        decimal? phMin,
        decimal? phMax,
        decimal? ghMin,
        decimal? ghMax,
        decimal? khMin,
        decimal? khMax,
        decimal? temperatureMin,
        decimal? temperatureMax,
        decimal? ammoniaMin,
        decimal? ammoniaMax,
        decimal? nitritesMin,
        decimal? nitritesMax,
        decimal? nitratesMin,
        decimal? nitratesMax,
        int? volumeMinLiters,
        string lightNeed,
        string co2Need,
        string fertilizationNeed,
        string growthSpeed,
        string recommendedPlacement,
        string behavior,
        string compatibility,
        string sourceUrl,
        string languageCode)
    {
        Id = id;
        Environment = environment;
        CommonName = commonName;
        CommonNameFr = commonNameFr;
        CommonNameEn = commonNameEn;
        CommonNameDe = commonNameDe;
        ScientificName = scientificName;
        PhMin = phMin;
        PhMax = phMax;
        GhMin = ghMin;
        GhMax = ghMax;
        KhMin = khMin;
        KhMax = khMax;
        TemperatureMin = temperatureMin;
        TemperatureMax = temperatureMax;
        AmmoniaMin = ammoniaMin;
        AmmoniaMax = ammoniaMax;
        NitritesMin = nitritesMin;
        NitritesMax = nitritesMax;
        NitratesMin = nitratesMin;
        NitratesMax = nitratesMax;
        VolumeMinLiters = volumeMinLiters;
        LightNeed = lightNeed;
        Co2Need = co2Need;
        FertilizationNeed = fertilizationNeed;
        GrowthSpeed = growthSpeed;
        RecommendedPlacement = recommendedPlacement;
        Behavior = behavior;
        Compatibility = compatibility;
        SourceUrl = sourceUrl;
        currentLanguage = languageCode is "en" or "de" ? languageCode : "fr";
        RowBackgroundBrush = Brushes.Transparent;
    }

    public Guid Id { get; }
    public PlantReferenceEnvironment Environment { get; }
    public string CommonName { get; }
    public string CommonNameFr { get; }
    public string CommonNameEn { get; }
    public string CommonNameDe { get; }
    public string ScientificName { get; }
    public decimal? PhMin { get; }
    public decimal? PhMax { get; }
    public decimal? GhMin { get; }
    public decimal? GhMax { get; }
    public decimal? KhMin { get; }
    public decimal? KhMax { get; }
    public decimal? TemperatureMin { get; }
    public decimal? TemperatureMax { get; }
    public decimal? AmmoniaMin { get; }
    public decimal? AmmoniaMax { get; }
    public decimal? NitritesMin { get; }
    public decimal? NitritesMax { get; }
    public decimal? NitratesMin { get; }
    public decimal? NitratesMax { get; }
    public int? VolumeMinLiters { get; }
    public string LightNeed { get; }
    public string Co2Need { get; }
    public string FertilizationNeed { get; }
    public string GrowthSpeed { get; }
    public string RecommendedPlacement { get; }
    public string Behavior { get; }
    public string Compatibility { get; }
    public string SourceUrl { get; }

    private Brush rowBackgroundBrush = Brushes.Transparent;
    private string currentLanguage = "fr";
    private bool isPhIncompatible;
    private bool isGhIncompatible;
    private bool isKhIncompatible;
    private bool isTemperatureIncompatible;
    private bool isAmmoniaIncompatible;
    private bool isNitritesIncompatible;
    private bool isNitratesIncompatible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Brush RowBackgroundBrush
    {
        get => rowBackgroundBrush;
        set
        {
            if (ReferenceEquals(rowBackgroundBrush, value))
            {
                return;
            }

            rowBackgroundBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackgroundBrush)));
        }
    }

    public string EnvironmentLabel => currentLanguage switch
    {
        "en" => Environment == PlantReferenceEnvironment.Marine ? "Marine" : "Freshwater",
        "de" => Environment == PlantReferenceEnvironment.Marine ? "Meerwasser" : "Suesswasser",
        _ => Environment == PlantReferenceEnvironment.Marine ? "Eau de mer" : "Eau douce"
    };

    public string LocalizedCommonName => currentLanguage switch
    {
        "en" => FirstNonEmpty(CommonNameEn, CommonName, CommonNameFr, CommonNameDe, ScientificName),
        "de" => FirstNonEmpty(CommonNameDe, CommonName, CommonNameFr, CommonNameEn, ScientificName),
        _ => FirstNonEmpty(CommonNameFr, CommonName, CommonNameEn, CommonNameDe, ScientificName)
    };

    public string LocalizedLightNeed => ReferenceTextLocalizer.Localize(LightNeed, currentLanguage);
    public string LocalizedCo2Need => ReferenceTextLocalizer.Localize(Co2Need, currentLanguage);
    public string LocalizedFertilizationNeed => ReferenceTextLocalizer.Localize(FertilizationNeed, currentLanguage);
    public string LocalizedGrowthSpeed => ReferenceTextLocalizer.Localize(GrowthSpeed, currentLanguage);
    public string LocalizedRecommendedPlacement => ReferenceTextLocalizer.Localize(RecommendedPlacement, currentLanguage);
    public string LocalizedBehavior => ReferenceTextLocalizer.Localize(Behavior, currentLanguage);
    public string LocalizedCompatibility => ReferenceTextLocalizer.Localize(Compatibility, currentLanguage);

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode is "en" or "de" ? languageCode : "fr";
        if (currentLanguage == normalized)
        {
            return;
        }

        currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnvironmentLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCommonName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedLightNeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCo2Need)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedFertilizationNeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedGrowthSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedRecommendedPlacement)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedBehavior)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCompatibility)));
    }

    public bool IsPhIncompatible
    {
        get => isPhIncompatible;
        set => SetFlag(ref isPhIncompatible, value, nameof(IsPhIncompatible));
    }

    public bool IsGhIncompatible
    {
        get => isGhIncompatible;
        set => SetFlag(ref isGhIncompatible, value, nameof(IsGhIncompatible));
    }

    public bool IsKhIncompatible
    {
        get => isKhIncompatible;
        set => SetFlag(ref isKhIncompatible, value, nameof(IsKhIncompatible));
    }

    public bool IsTemperatureIncompatible
    {
        get => isTemperatureIncompatible;
        set => SetFlag(ref isTemperatureIncompatible, value, nameof(IsTemperatureIncompatible));
    }

    public bool IsAmmoniaIncompatible
    {
        get => isAmmoniaIncompatible;
        set => SetFlag(ref isAmmoniaIncompatible, value, nameof(IsAmmoniaIncompatible));
    }

    public bool IsNitritesIncompatible
    {
        get => isNitritesIncompatible;
        set => SetFlag(ref isNitritesIncompatible, value, nameof(IsNitritesIncompatible));
    }

    public bool IsNitratesIncompatible
    {
        get => isNitratesIncompatible;
        set => SetFlag(ref isNitratesIncompatible, value, nameof(IsNitratesIncompatible));
    }

    private void SetFlag(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

public sealed class AnimalReferenceItem : INotifyPropertyChanged
{
    public AnimalReferenceItem(
        Guid id,
        AnimalReferenceEnvironment environment,
        AnimalReferenceGroup group,
        string commonName,
        string commonNameFr,
        string commonNameEn,
        string commonNameDe,
        string scientificName,
        decimal? phMin,
        decimal? phMax,
        decimal? ghMin,
        decimal? ghMax,
        decimal? khMin,
        decimal? khMax,
        decimal? temperatureMin,
        decimal? temperatureMax,
        decimal? ammoniaMin,
        decimal? ammoniaMax,
        decimal? nitritesMin,
        decimal? nitritesMax,
        decimal? nitratesMin,
        decimal? nitratesMax,
        int? volumeMinLiters,
        string behavior,
        string compatibility,
        string sourceUrl,
        string languageCode)
    {
        Id = id;
        Environment = environment;
        Group = group;
        CommonName = commonName;
        CommonNameFr = commonNameFr;
        CommonNameEn = commonNameEn;
        CommonNameDe = commonNameDe;
        ScientificName = scientificName;
        PhMin = phMin;
        PhMax = phMax;
        GhMin = ghMin;
        GhMax = ghMax;
        KhMin = khMin;
        KhMax = khMax;
        TemperatureMin = temperatureMin;
        TemperatureMax = temperatureMax;
        AmmoniaMin = ammoniaMin;
        AmmoniaMax = ammoniaMax;
        NitritesMin = nitritesMin;
        NitritesMax = nitritesMax;
        NitratesMin = nitratesMin;
        NitratesMax = nitratesMax;
        VolumeMinLiters = volumeMinLiters;
        Behavior = behavior;
        Compatibility = compatibility;
        SourceUrl = sourceUrl;
        currentLanguage = languageCode is "en" or "de" ? languageCode : "fr";
        RowBackgroundBrush = Brushes.Transparent;
    }

    public Guid Id { get; }
    public AnimalReferenceEnvironment Environment { get; }
    public AnimalReferenceGroup Group { get; }
    public string CommonName { get; }
    public string CommonNameFr { get; }
    public string CommonNameEn { get; }
    public string CommonNameDe { get; }
    public string ScientificName { get; }
    public decimal? PhMin { get; }
    public decimal? PhMax { get; }
    public decimal? GhMin { get; }
    public decimal? GhMax { get; }
    public decimal? KhMin { get; }
    public decimal? KhMax { get; }
    public decimal? TemperatureMin { get; }
    public decimal? TemperatureMax { get; }
    public decimal? AmmoniaMin { get; }
    public decimal? AmmoniaMax { get; }
    public decimal? NitritesMin { get; }
    public decimal? NitritesMax { get; }
    public decimal? NitratesMin { get; }
    public decimal? NitratesMax { get; }
    public int? VolumeMinLiters { get; }
    public string Behavior { get; }
    public string Compatibility { get; }
    public string SourceUrl { get; }

    private Brush rowBackgroundBrush = Brushes.Transparent;
    private string currentLanguage = "fr";
    private bool isPhIncompatible;
    private bool isGhIncompatible;
    private bool isKhIncompatible;
    private bool isTemperatureIncompatible;
    private bool isAmmoniaIncompatible;
    private bool isNitritesIncompatible;
    private bool isNitratesIncompatible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Brush RowBackgroundBrush
    {
        get => rowBackgroundBrush;
        set
        {
            if (ReferenceEquals(rowBackgroundBrush, value))
            {
                return;
            }

            rowBackgroundBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackgroundBrush)));
        }
    }

    public string EnvironmentLabel => currentLanguage switch
    {
        "en" => Environment == AnimalReferenceEnvironment.Marine ? "Marine" : "Freshwater",
        "de" => Environment == AnimalReferenceEnvironment.Marine ? "Meerwasser" : "Suesswasser",
        _ => Environment == AnimalReferenceEnvironment.Marine ? "Eau de mer" : "Eau douce"
    };

    public string GroupLabel => currentLanguage switch
    {
        "en" => Group switch
        {
            AnimalReferenceGroup.Shrimp => "Shrimp",
            AnimalReferenceGroup.Snail => "Molluscs",
            AnimalReferenceGroup.Other => "Other",
            _ => "Fish"
        },
        "de" => Group switch
        {
            AnimalReferenceGroup.Shrimp => "Garnelen",
            AnimalReferenceGroup.Snail => "Mollusken",
            AnimalReferenceGroup.Other => "Andere",
            _ => "Fische"
        },
        _ => Group switch
        {
            AnimalReferenceGroup.Shrimp => "Crevettes",
            AnimalReferenceGroup.Snail => "Mollusques",
            AnimalReferenceGroup.Other => "Autres",
            _ => "Poissons"
        }
    };

    public string LocalizedCommonName => currentLanguage switch
    {
        "en" => FirstNonEmpty(CommonNameEn, CommonName, CommonNameFr, CommonNameDe, ScientificName),
        "de" => FirstNonEmpty(CommonNameDe, CommonName, CommonNameFr, CommonNameEn, ScientificName),
        _ => FirstNonEmpty(CommonNameFr, CommonName, CommonNameEn, CommonNameDe, ScientificName)
    };

    public string LocalizedBehavior => ReferenceTextLocalizer.Localize(Behavior, currentLanguage);
    public string LocalizedCompatibility => ReferenceTextLocalizer.Localize(Compatibility, currentLanguage);

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode is "en" or "de" ? languageCode : "fr";
        if (currentLanguage == normalized)
        {
            return;
        }

        currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnvironmentLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCommonName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedBehavior)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCompatibility)));
    }

    public bool IsPhIncompatible { get => isPhIncompatible; set => SetFlag(ref isPhIncompatible, value, nameof(IsPhIncompatible)); }
    public bool IsGhIncompatible { get => isGhIncompatible; set => SetFlag(ref isGhIncompatible, value, nameof(IsGhIncompatible)); }
    public bool IsKhIncompatible { get => isKhIncompatible; set => SetFlag(ref isKhIncompatible, value, nameof(IsKhIncompatible)); }
    public bool IsTemperatureIncompatible { get => isTemperatureIncompatible; set => SetFlag(ref isTemperatureIncompatible, value, nameof(IsTemperatureIncompatible)); }
    public bool IsAmmoniaIncompatible { get => isAmmoniaIncompatible; set => SetFlag(ref isAmmoniaIncompatible, value, nameof(IsAmmoniaIncompatible)); }
    public bool IsNitritesIncompatible { get => isNitritesIncompatible; set => SetFlag(ref isNitritesIncompatible, value, nameof(IsNitritesIncompatible)); }
    public bool IsNitratesIncompatible { get => isNitratesIncompatible; set => SetFlag(ref isNitratesIncompatible, value, nameof(IsNitratesIncompatible)); }

    private void SetFlag(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

internal static class ReferenceTextLocalizer
{
    private static readonly Dictionary<string, (string English, string German)> Values = new(StringComparer.Ordinal)
    {
        ["actif"] = ("Active", "Aktiv"),
        ["arriere"] = ("Background", "Hintergrund"),
        ["a surveiller avec coraux"] = ("Monitor with corals", "Mit Korallen beobachten"),
        ["avant"] = ("Foreground", "Vordergrund"),
        ["avant / decor"] = ("Foreground / decor", "Vordergrund / Dekor"),
        ["avant / milieu"] = ("Foreground / midground", "Vordergrund / Mitte"),
        ["avec especes de taille suffisante"] = ("With sufficiently sized species", "Mit ausreichend grossen Arten"),
        ["bac specifique recommande"] = ("Species tank recommended", "Artbecken empfohlen"),
        ["banc paisible"] = ("Peaceful schooling fish", "Friedlicher Schwarmfisch"),
        ["bonne avec poissons calmes"] = ("Good with calm fish", "Gut mit ruhigen Fischen"),
        ["bonne avec poissons phytophages"] = ("Good with herbivorous fish", "Gut mit pflanzenfressenden Fischen"),
        ["bonne avec taille reguliere"] = ("Good with regular trimming", "Gut bei regelmaessigem Rueckschnitt"),
        ["bonne avec vivipares"] = ("Good with livebearers", "Gut mit lebendgebaerenden Fischen"),
        ["bonne contre nitrates"] = ("Good against nitrates", "Gut gegen Nitratwerte"),
        ["bonne en aquascaping"] = ("Good for aquascaping", "Gut fuer Aquascaping"),
        ["bonne en bac calme"] = ("Good in calm tanks", "Gut in ruhigen Becken"),
        ["bonne en bac communautaire"] = ("Good in community tanks", "Gut im Gesellschaftsbecken"),
        ["bonne en communautaire"] = ("Good in community tanks", "Gut im Gesellschaftsbecken"),
        ["bonne en premier plan"] = ("Good foreground plant", "Gute Vordergrundpflanze"),
        ["bonne pour alevins"] = ("Good for fry", "Gut fuer Jungfische"),
        ["bonne pour debutants"] = ("Good for beginners", "Gut fuer Anfaenger"),
        ["bulbe"] = ("Bulb", "Knolle"),
        ["calcifiante"] = ("Calcifying", "Verkalkend"),
        ["compacte"] = ("Compact", "Kompakt"),
        ["communautaire calme"] = ("Calm community tank", "Ruhiges Gesellschaftsbecken"),
        ["compatibilite elevee"] = ("High compatibility", "Hohe Vertraeglichkeit"),
        ["compatible communautaire"] = ("Community compatible", "Gemeinschaftsbecken geeignet"),
        ["compatible recifal"] = ("Reef compatible", "Riffgeeignet"),
        ["demande eau propre"] = ("Requires clean water", "Benoetigt sauberes Wasser"),
        ["en groupe 6+"] = ("In groups of 6+", "In Gruppen ab 6"),
        ["epiphyte"] = ("Epiphyte", "Epiphyt"),
        ["espace important"] = ("Needs significant space", "Braucht viel Platz"),
        ["eviter poissons agressifs"] = ("Avoid aggressive fish", "Aggressive Fische vermeiden"),
        ["eviter gros predateurs"] = ("Avoid large predators", "Grosse Raeuber vermeiden"),
        ["excellente anti-algues"] = ("Excellent against algae", "Ausgezeichnet gegen Algen"),
        ["excellente pour crevettes"] = ("Excellent for shrimp", "Ausgezeichnet fuer Garnelen"),
        ["faible"] = ("Low", "Niedrig"),
        ["faible a moyenne"] = ("Low to medium", "Niedrig bis mittel"),
        ["feuille en dentelle"] = ("Lace leaves", "Spitzenartige Blaetter"),
        ["feuilles coriaces"] = ("Tough leaves", "Derbe Blaetter"),
        ["feuilles ondulees"] = ("Wavy leaves", "Gewellte Blaetter"),
        ["flottante / arriere"] = ("Floating / background", "Schwimmpflanze / Hintergrund"),
        ["forte"] = ("High", "Stark"),
        ["fougere aquatique"] = ("Aquatic fern", "Wasserfarn"),
        ["fougere epiphyte"] = ("Epiphytic fern", "Epiphytischer Farn"),
        ["gazonnante"] = ("Carpeting", "Teppichbildend"),
        ["gregaire"] = ("Gregarious", "Gesellig"),
        ["gregaire de fond"] = ("Gregarious bottom-dweller", "Geselliger Bodenbewohner"),
        ["gregaire paisible"] = ("Peaceful gregarious species", "Friedliche gesellige Art"),
        ["grandes feuilles rubanees"] = ("Large ribbon-like leaves", "Grosse bandfoermige Blaetter"),
        ["hepatique flottante"] = ("Floating liverwort", "Schwimmendes Lebermoos"),
        ["hierarchique"] = ("Hierarchical", "Hierarchisch"),
        ["lente"] = ("Slow", "Langsam"),
        ["longues feuilles"] = ("Long leaves", "Lange Blaetter"),
        ["male seul"] = ("Single male", "Maennchen einzeln"),
        ["milieu"] = ("Midground", "Mitte"),
        ["milieu / arriere"] = ("Midground / background", "Mitte / Hintergrund"),
        ["mousse"] = ("Moss", "Moos"),
        ["moyen"] = ("Medium", "Mittel"),
        ["moyenne"] = ("Medium", "Mittel"),
        ["moyenne a elevee"] = ("Medium to high", "Mittel bis hoch"),
        ["moyenne a forte"] = ("Medium to high", "Mittel bis stark"),
        ["nageur rapide"] = ("Fast swimmer", "Schneller Schwimmer"),
        ["paisible algivore"] = ("Peaceful algae grazer", "Friedlicher Algenfresser"),
        ["paisible detritivore"] = ("Peaceful detritus grazer", "Friedlicher Detritusfresser"),
        ["paisible nettoyeuse"] = ("Peaceful cleaner", "Friedlicher Putzer"),
        ["optionnel"] = ("Optional", "Optional"),
        ["peut devenir envahissante"] = ("Can become invasive", "Kann wuchernd werden"),
        ["peut etre territorial"] = ("Can be territorial", "Kann territorial sein"),
        ["peut flotter"] = ("Can float", "Kann schwimmen"),
        ["preferer courant doux"] = ("Prefers gentle current", "Bevorzugt sanfte Stroemung"),
        ["racine / roche"] = ("Root / rock", "Wurzel / Stein"),
        ["racines / roches"] = ("Roots / rocks", "Wurzeln / Steine"),
        ["rapide"] = ("Fast", "Schnell"),
        ["refuge / decor"] = ("Refugium / decor", "Refugium / Dekor"),
        ["robuste"] = ("Hardy", "Robust"),
        ["roche / racine"] = ("Rock / root", "Stein / Wurzel"),
        ["roches vivantes"] = ("Live rock", "Lebende Steine"),
        ["rosette compacte"] = ("Compact rosette", "Kompakte Rosette"),
        ["rosette ondulee"] = ("Wavy rosette", "Gewellte Rosette"),
        ["rouge decorative"] = ("Decorative red", "Dekorativ rot"),
        ["surface / arriere"] = ("Surface / background", "Oberflaeche / Hintergrund"),
        ["surface / gazon"] = ("Surface / carpet", "Oberflaeche / Teppich"),
        ["tapis rampant"] = ("Creeping carpet", "Kriechender Teppich"),
        ["territorial"] = ("Territorial", "Territorial"),
        ["territorial modere"] = ("Moderately territorial", "Maessig territorial"),
        ["tige"] = ("Stem plant", "Stengelpflanze"),
        ["tige fine"] = ("Fine stem plant", "Feine Stengelpflanze"),
        ["tres adaptable"] = ("Very adaptable", "Sehr anpassungsfaehig"),
        ["tres facile"] = ("Very easy", "Sehr einfach"),
        ["tres rapide"] = ("Very fast", "Sehr schnell"),
        ["vif"] = ("Lively", "Lebhaft")
    };

    public static string Localize(string value, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(value) || languageCode == "fr")
        {
            return value;
        }

        return Values.TryGetValue(Normalize(value), out var localized)
            ? languageCode == "de" ? localized.German : localized.English
            : value;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace('à', 'a')
            .Replace('â', 'a')
            .Replace('ä', 'a')
            .Replace('ç', 'c')
            .Replace('é', 'e')
            .Replace('è', 'e')
            .Replace('ê', 'e')
            .Replace('ë', 'e')
            .Replace('î', 'i')
            .Replace('ï', 'i')
            .Replace('ô', 'o')
            .Replace('ö', 'o')
            .Replace('ù', 'u')
            .Replace('û', 'u')
            .Replace('ü', 'u');

        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
    }
}

public sealed class TrendParameterOption : INotifyPropertyChanged
{
    private bool isSelected;
    private string name;

    public TrendParameterOption(string parameterKey, string name, bool isSelected)
    {
        ParameterKey = parameterKey;
        this.name = name;
        this.isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ParameterKey { get; }

    public string Name
    {
        get => name;
        private set
        {
            if (name == value)
            {
                return;
            }

            name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public void SetDisplayName(string value)
    {
        Name = value;
    }
}
