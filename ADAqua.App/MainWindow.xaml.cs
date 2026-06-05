using ADAqua.Domain;
using ADAqua.Infrastructure;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

    private readonly MainWindowViewModel viewModel = new();
    private readonly Dictionary<string, Dictionary<string, string>> localizedTexts = CreateLocalizedTexts();
    private MySqlAquariumRepository? repository;
    private string? activeConnectionString;
    private bool isApplyingSettings;
    private bool isInlineGridPersistQueued;
    private bool isInlineGridPersisting;
    private string currentLanguage = LanguageFrench;
    private string currentTheme = ThemeLight;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetTextProvider(T);
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        AppLogger.Info("Application started.");

        var appSettings = AppSettingsStore.Load();
        var startupLanguage = NormalizeLanguageCode(appSettings?.LanguageCode);
        var startupTheme = NormalizeThemeCode(appSettings?.ThemeCode);

        ApplyTheme(startupTheme);
        ApplyLanguage(startupLanguage);

        isApplyingSettings = true;
        SelectComboByTag(LanguageComboBox, startupLanguage);
        SelectComboByTag(ThemeComboBox, startupTheme);
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

        viewModel.StatusMessage = aquariums.Count == 0
            ? T("StatusConnectedNoAquarium")
            : string.Format(T("StatusConnectedAquariumCount"), aquariums.Count);
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
            QueueInlineGridPersist(T("StatusPlantSaved"), "Plant inline edit persist failed.");
        }
    }

    private void PlantGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPlantSaved"), "Plant row edit persist failed.");
        }
    }

    private void PopulationGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPopulationSaved"), "Population inline edit persist failed.");
        }
    }

    private void PopulationGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            QueueInlineGridPersist(T("StatusPopulationSaved"), "Population row edit persist failed.");
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
            DispatcherPriority.Background);
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

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string languageCode)
        {
            return;
        }

        ApplyLanguage(languageCode);
        viewModel.StatusMessage = T("StatusLanguageChanged");
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isApplyingSettings || ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string themeCode)
        {
            return;
        }

        ApplyTheme(themeCode);
        viewModel.StatusMessage = T("StatusThemeChanged");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            AppSettingsStore.Save(new AppSettings
            {
                LanguageCode = currentLanguage,
                ThemeCode = currentTheme
            });
        }
        catch (IOException)
        {
            // Ignore close-time persistence errors to avoid blocking shutdown.
            AppLogger.Error("App settings save failed on close (IO).");
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore close-time persistence errors to avoid blocking shutdown.
            AppLogger.Error("App settings save failed on close (Unauthorized).");
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
            viewModel.StatusMessage = "Recherche plantes annulee.";
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
            viewModel.StatusMessage = $"Recherche plantes lancee avec au moins {minimumParameterGroups.Value} groupes de parametres.";
            var imported = await repository.ImportPlantReferencesFromWebAsync(progress, minimumParameterGroups.Value);
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = string.IsNullOrWhiteSpace(lastProgressMessage)
                ? $"{imported} nouvelles plantes importees depuis le web."
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
        viewModel.StatusMessage = "Compatibilite plantes evaluee sur la derniere mesure de l'aquarium selectionne.";
    }

    private void PlantReferenceApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyPlantReferenceFilters();
        viewModel.StatusMessage = "Filtres plantes appliques.";
    }

    private void PlantReferenceResetFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ResetPlantReferenceFilters();
        viewModel.StatusMessage = "Filtres plantes reinitialises.";
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
            viewModel.StatusMessage = "Selectionner une reference plante a supprimer.";
            return;
        }

        var reference = viewModel.SelectedPlantReference;
        var result = MessageBox.Show(
            $"Supprimer la reference \"{reference.ScientificName}\" ?",
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium.Id);
            viewModel.StatusMessage = "Reference plante supprimee.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Suppression reference impossible: {ex.Message}";
            AppLogger.Error("Delete plant reference failed.", ex);
        }
    }

    private async void PlantReferenceReset_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant reinitialisation.";
            return;
        }

        var result = MessageBox.Show(
            "Reinitialiser le referentiel plantes ?",
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = $"Referentiel plantes reinitialise (avant: {before}, apres: {after}).";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Reinitialisation plantes impossible: {ex.Message}";
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
            viewModel.StatusMessage = "Recherche especes annulee.";
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
            viewModel.StatusMessage = $"Recherche especes lancee avec au moins {minimumParameterGroups.Value} groupes de parametres.";
            var imported = await repository.ImportAnimalReferencesFromWebAsync(progress, minimumParameterGroups.Value);
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = string.IsNullOrWhiteSpace(lastProgressMessage)
                ? $"{imported} nouvelles especes importees depuis le web."
                : lastProgressMessage;
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Import web des especes impossible: {ex.Message}";
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
        viewModel.StatusMessage = "Compatibilite especes evaluee sur la derniere mesure de l'aquarium selectionne.";
    }

    private void AnimalReferenceApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ApplyAnimalReferenceFilters();
        viewModel.StatusMessage = "Filtres especes appliques.";
    }

    private void AnimalReferenceResetFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ResetAnimalReferenceFilters();
        viewModel.StatusMessage = "Filtres especes reinitialises.";
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
            viewModel.StatusMessage = "Selectionner une reference plante a modifier.";
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = "Reference plante modifiee.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Modification reference plante impossible: {ex.Message}";
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

        var environmentInput = new ComboBox
        {
            Width = 260,
            Height = 32,
            Background = inputBrush,
            Foreground = textBrush,
            BorderBrush = borderBrush
        };
        environmentInput.Items.Add(new ComboBoxItem { Content = "Eau douce tropicale", Tag = PlantReferenceEnvironment.FreshwaterTropical });
        environmentInput.Items.Add(new ComboBoxItem { Content = "Eau de mer", Tag = PlantReferenceEnvironment.Marine });
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
        AddEditRow(form, "Temperature min", temperatureMinInput, textBrush);
        AddEditRow(form, "Temperature max", temperatureMaxInput, textBrush);
        AddEditRow(form, "Amoniac min", ammoniaMinInput, textBrush);
        AddEditRow(form, "Amoniac max", ammoniaMaxInput, textBrush);
        AddEditRow(form, "Nitrites min", nitritesMinInput, textBrush);
        AddEditRow(form, "Nitrites max", nitritesMaxInput, textBrush);
        AddEditRow(form, "Nitrates min", nitratesMinInput, textBrush);
        AddEditRow(form, "Nitrates max", nitratesMaxInput, textBrush);
        AddEditRow(form, "Volume min (L)", volumeInput, textBrush);
        AddEditRow(form, "Lumiere", lightInput, textBrush);
        AddEditRow(form, "CO2", co2Input, textBrush);
        AddEditRow(form, "Fertilisation", fertilizationInput, textBrush);
        AddEditRow(form, "Croissance", growthInput, textBrush);
        AddEditRow(form, "Emplacement", placementInput, textBrush);
        AddEditRow(form, "Comportement", behaviorInput, textBrush);
        AddEditRow(form, "Compatibilites", compatibilityInput, textBrush);
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
            Text = "Corriger la reference plante selectionnee. Les champs numeriques vides resteront inconnus en base.",
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 0)
        });
        content.Children.Add(form);
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Modifier une reference plante",
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
                || !TryReadDecimal(temperatureMinInput, "Temperature min", errorText, out var temperatureMin)
                || !TryReadDecimal(temperatureMaxInput, "Temperature max", errorText, out var temperatureMax)
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

            var selectedEnvironment = environmentInput.SelectedItem is ComboBoxItem { Tag: PlantReferenceEnvironment tag }
                ? tag
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
            viewModel.StatusMessage = "Selectionner une reference animale a modifier.";
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = "Reference animale modifiee.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Modification reference impossible: {ex.Message}";
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

        var environmentInput = new ComboBox
        {
            Width = 260,
            Height = 32,
            Background = inputBrush,
            Foreground = textBrush,
            BorderBrush = borderBrush
        };
        environmentInput.Items.Add(new ComboBoxItem { Content = "Eau douce tropicale", Tag = AnimalReferenceEnvironment.FreshwaterTropical });
        environmentInput.Items.Add(new ComboBoxItem { Content = "Eau de mer", Tag = AnimalReferenceEnvironment.Marine });
        environmentInput.SelectedIndex = source.Environment == AnimalReferenceEnvironment.Marine ? 1 : 0;

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
        AddEditRow(form, "Temperature min", temperatureMinInput, textBrush);
        AddEditRow(form, "Temperature max", temperatureMaxInput, textBrush);
        AddEditRow(form, "Amoniac min", ammoniaMinInput, textBrush);
        AddEditRow(form, "Amoniac max", ammoniaMaxInput, textBrush);
        AddEditRow(form, "Nitrites min", nitritesMinInput, textBrush);
        AddEditRow(form, "Nitrites max", nitritesMaxInput, textBrush);
        AddEditRow(form, "Nitrates min", nitratesMinInput, textBrush);
        AddEditRow(form, "Nitrates max", nitratesMaxInput, textBrush);
        AddEditRow(form, "Volume min (L)", volumeInput, textBrush);
        AddEditRow(form, "Comportement", behaviorInput, textBrush);
        AddEditRow(form, "Compatibilites", compatibilityInput, textBrush);
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
            Text = "Corriger la reference selectionnee. Les champs numeriques vides resteront inconnus en base.",
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 0)
        });
        content.Children.Add(form);
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Modifier une reference animale",
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
                || !TryReadDecimal(temperatureMinInput, "Temperature min", errorText, out var temperatureMin)
                || !TryReadDecimal(temperatureMaxInput, "Temperature max", errorText, out var temperatureMax)
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

            var selectedEnvironment = environmentInput.SelectedItem is ComboBoxItem { Tag: AnimalReferenceEnvironment tag }
                ? tag
                : AnimalReferenceEnvironment.FreshwaterTropical;

            result = new AnimalReference
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

        ShowEditError(errorText, $"Valeur entiere invalide pour {label}.");
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
            viewModel.StatusMessage = "Selectionner une reference animale a supprimer.";
            return;
        }

        var reference = viewModel.SelectedAnimalReference;
        var result = MessageBox.Show(
            $"Supprimer la reference \"{reference.ScientificName}\" ?",
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium.Id);
            viewModel.StatusMessage = "Reference animale supprimee.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Suppression reference impossible: {ex.Message}";
            AppLogger.Error("Delete animal reference failed.", ex);
        }
    }

    private async void AnimalReferenceReset_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configurer MySQL avant reinitialisation.";
            return;
        }

        var result = MessageBox.Show(
            "Reinitialiser le referentiel population ?",
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
            await LoadAquariumsAsync(viewModel.SelectedAquarium?.Id);
            viewModel.StatusMessage = $"Referentiel population reinitialise (avant: {before}, apres: {after}).";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Reinitialisation population impossible: {ex.Message}";
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
        viewModel.StatusMessage = "Log rafraichi.";
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
        Resources["AppBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#101617" : "#F3F7F8"));
        Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#1A2426" : "#FFFFFF"));
        Resources["CardBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#355055" : "#C8D8DA"));
        Resources["HeaderBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#0A2C31" : "#0E3F46"));
        Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E3F0F1" : "#172326"));
        Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#9AB2B6" : "#53696E"));
        Resources["TextOnHeaderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#D7ECEE" : "#D8EFF0"));
        Resources["ButtonPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#1A7F84" : "#156B6F"));
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
        Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#4C9EA4" : "#4C9EA4"));
        Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#172326" : "#172326"));
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
            ["UiAppSubtitle"] = "Gestion des aquariums, parametres d'eau, plantes et population",
            ["UiSectionAquariums"] = "Aquariums",
            ["UiButtonNewAquarium"] = "Nouvel aquarium",
            ["UiButtonDeleteAquarium"] = "Supprimer aquarium",
            ["UiTabSheet"] = "Fiche",
            ["UiTabParameters"] = "Parametres",
            ["UiTabPlants"] = "Plantes",
            ["UiTabPlantReference"] = "Referentiel plantes",
            ["UiTabPopulation"] = "Population",
            ["UiTabSettings"] = "Parametrages",
            ["UiLabelName"] = "Nom",
            ["UiLabelVolume"] = "Volume (L)",
            ["UiLabelWaterType"] = "Type d'eau",
            ["UiLabelStartedOn"] = "Mise en eau",
            ["UiLabelNotes"] = "Notes",
            ["UiLabelAmmonia"] = "Amoniac mg/L",
            ["UiLabelNitrites"] = "Nitrites mg/L",
            ["UiLabelNitrates"] = "Nitrates mg/L",
            ["UiLabelPh"] = "pH",
            ["UiLabelGh"] = "GH",
            ["UiLabelKh"] = "KH",
            ["UiLabelTemperature"] = "Temperature C",
            ["UiButtonAddMeasurement"] = "Ajouter la mesure",
            ["UiButtonDuplicateMeasurement"] = "Dupliquer la mesure",
            ["UiButtonDeleteMeasurement"] = "Supprimer la mesure",
            ["UiGridDate"] = "Date",
            ["UiPlantCommonName"] = "Nom courant",
            ["UiPlantReferenceChoice"] = "Referentiel",
            ["UiPlantScientificName"] = "Nom scientifique",
            ["UiPlantGrowth"] = "Croissance",
            ["UiGrowthSlow"] = "Lente",
            ["UiGrowthMedium"] = "Moyenne",
            ["UiGrowthFast"] = "Rapide",
            ["UiPlantLightNeed"] = "Lumiere",
            ["UiLightLow"] = "Faible",
            ["UiLightMedium"] = "Moyenne",
            ["UiLightHigh"] = "Forte",
            ["UiButtonAddPlant"] = "Ajouter la plante",
            ["UiButtonDeletePlant"] = "Supprimer la plante",
            ["UiGridScientific"] = "Scientifique",
            ["UiGridGrowth"] = "Croissance",
            ["UiPopulationSpecies"] = "Espece",
            ["UiAnimalReferenceChoice"] = "Referentiel faune",
            ["UiPopulationType"] = "Type",
            ["UiPopulationQuantity"] = "Quantite",
            ["UiButtonAddPopulation"] = "Ajouter la population",
            ["UiButtonDeletePopulation"] = "Supprimer la population",
            ["UiDbActionsHelp"] = "Actions base de donnees et maintenance.",
            ["UiButtonConfigureMySql"] = "Configurer MySQL",
            ["UiButtonInitializeMySql"] = "Initialiser MySQL",
            ["UiButtonSave"] = "Sauvegarder",
            ["UiLabelLanguage"] = "Langue",
            ["UiLabelTheme"] = "Theme",
            ["UiHealthCharts"] = "Graphiques d'evolution",
            ["UiHealthPeriod"] = "Periode",
            ["UiHealthPeriod7"] = "7 jours",
            ["UiHealthPeriod30"] = "30 jours",
            ["UiHealthPeriod90"] = "90 jours",
            ["UiHealthPeriodAll"] = "Tout l'historique",
            ["UiHealthParameters"] = "Parametres a afficher",
            ["UiHealthNoChartData"] = "Pas assez de mesures pour tracer un graphe.",
            ["UiLangFrench"] = "Francais",
            ["UiLangEnglish"] = "Anglais",
            ["UiLangGerman"] = "Allemand",
            ["UiThemeLight"] = "Clair",
            ["UiThemeDark"] = "Sombre",
            ["UiReferenceSearchCriteriaTitle"] = "Criteres de recherche",
            ["UiReferenceSearchCriteriaIntro"] = "Choisis les criteres utilises pour filtrer les fiches candidates avant insertion dans le referentiel.",
            ["UiReferenceSearchMinimumParameterGroups"] = "Nombre minimal de groupes de parametres",
            ["UiReferenceSearchMinimumParameterGroupsHelp"] = "Valeur entre 1 et 8. Plus le nombre est eleve, plus les especes importees seront documentees.",
            ["UiReferenceSearchInvalidMinimum"] = "Saisis un nombre entier entre 1 et 8.",
            ["UiDialogOk"] = "OK",
            ["UiDialogCancel"] = "Annuler",
            ["StatusReady"] = "Pret. MySQL est optionnel au demarrage pour garder l'application utilisable hors ligne.",
            ["StatusMySqlNotConfigured"] = "MySQL non configure. Utilise Configurer MySQL pour enregistrer une connexion locale.",
            ["StatusMySqlConfiguredSecure"] = "MySQL configure depuis la configuration locale securisee.",
            ["StatusMySqlConfiguredEnv"] = "MySQL configure via ADAQUA_MYSQL_CONNECTION_STRING.",
            ["StatusReadFailed"] = "Lecture MySQL indisponible:",
            ["StatusMySqlConfigSaved"] = "Configuration MySQL enregistree et active.",
            ["StatusConfigSavedReadFailed"] = "Configuration enregistree, mais lecture MySQL impossible:",
            ["StatusConfigureBeforeInit"] = "Configure MySQL avant d'initialiser le schema.",
            ["StatusSchemaInitialized"] = "Schema MySQL initialise.",
            ["StatusInitializationFailed"] = "Initialisation MySQL impossible:",
            ["StatusConnectedNoAquarium"] = "MySQL connecte. Aucun aquarium en base pour le moment.",
            ["StatusConnectedAquariumCount"] = "MySQL connecte. {0} aquarium(s) charge(s).",
            ["StatusLocalModeConfigure"] = "Mode local: configure MySQL pour sauvegarder en base.",
            ["StatusSaveInvalidInput"] = "Sauvegarde impossible: corrige les erreurs de saisie avant d'enregistrer.",
            ["StatusAquariumSaved"] = "Aquarium sauvegarde dans MySQL.",
            ["StatusSaveFailed"] = "Enregistrement MySQL impossible:",
            ["StatusNewAquariumSaved"] = "Nouvel aquarium enregistre.",
            ["StatusAquariumDeleted"] = "Aquarium supprime.",
            ["StatusAquariumDeleteFailed"] = "Suppression de l'aquarium impossible:",
            ["StatusSelectMeasurementDelete"] = "Selectionne une mesure a supprimer.",
            ["StatusSelectMeasurementDuplicate"] = "Selectionne une mesure a dupliquer.",
            ["StatusMeasurementSaved"] = "Mesure d'eau enregistree.",
            ["StatusMeasurementDuplicated"] = "Mesure d'eau dupliquee et enregistree.",
            ["StatusMeasurementDeleted"] = "Mesure d'eau supprimee.",
            ["StatusSelectPlantDelete"] = "Selectionne une plante a supprimer.",
            ["StatusPlantSaved"] = "Plante enregistree.",
            ["StatusPlantDeleted"] = "Plante supprimee.",
            ["StatusSelectPopulationDelete"] = "Selectionne une population a supprimer.",
            ["StatusPopulationSaved"] = "Population enregistree.",
            ["StatusPopulationDeleted"] = "Population supprimee.",
            ["StatusLanguageChanged"] = "Langue appliquee.",
            ["StatusThemeChanged"] = "Theme applique.",
            ["HealthStatusNoData"] = "Aucune mesure",
            ["HealthStatusOk"] = "Stable",
            ["HealthStatusWarning"] = "Alerte moderee",
            ["HealthStatusCritical"] = "Alerte critique",
            ["HealthAlertNoData"] = "N/A",
            ["HealthAlertOk"] = "OK",
            ["HealthAlertWarning"] = "A surveiller",
            ["HealthAlertCritical"] = "Critique",
            ["HealthActionNoData"] = "Ajoute une mesure d'eau pour activer le suivi de sante.",
            ["HealthActionOk"] = "Parametres dans les plages cibles. Continuer la routine actuelle.",
            ["HealthActionWarningNitrates"] = "Prevoir un changement d'eau partiel pour reduire les nitrates.",
            ["HealthActionWarningPh"] = "Verifier le pH et ajuster progressivement si necessaire.",
            ["HealthActionWarningHardness"] = "Verifier GH/KH et adapter l'eau de remplacement.",
            ["HealthActionWarningGeneric"] = "Surveiller l'evolution sur les prochaines mesures.",
            ["HealthActionCriticalWaterChange"] = "Effectuer un changement d'eau rapide et verifier filtration/aeration.",
            ["HealthActionCriticalFeeding"] = "Reduire la nourriture temporairement pour limiter la charge azotee.",
            ["HealthActionCriticalTemperature"] = "Corriger la temperature (chauffage/refroidissement) sans variation brutale.",
            ["HealthActionCriticalGeneric"] = "Analyser l'eau et stabiliser les parametres critiques en priorite.",
            ["ConfirmDeleteTitle"] = "Confirmation de suppression",
            ["ConfirmDeleteAquarium"] = "Supprimer l'aquarium \"{0}\" et toutes ses donnees associees ?",
            ["ConfirmDeleteMeasurement"] = "Supprimer la mesure du {0:g} ?",
            ["ConfirmDeletePlant"] = "Supprimer la plante \"{0}\" ?",
            ["ConfirmDeletePopulation"] = "Supprimer \"{0}\" de la population ?",
            ["DefaultWaterType"] = "Eau douce",
            ["DefaultMainAquarium"] = "Bac principal",
            ["DefaultMainAquariumNote"] = "Premier aquarium ADAqua.",
            ["DefaultPlantLight"] = "Faible",
            ["DefaultNeonName"] = "Neon bleu"
        };

        var en = new Dictionary<string, string>(fr)
        {
            ["UiAppSubtitle"] = "Manage aquariums, water parameters, plants and population",
            ["UiSectionAquariums"] = "Aquariums",
            ["UiButtonNewAquarium"] = "New aquarium",
            ["UiButtonDeleteAquarium"] = "Delete aquarium",
            ["UiTabSheet"] = "Overview",
            ["UiTabParameters"] = "Parameters",
            ["UiTabPlants"] = "Plants",
            ["UiTabPlantReference"] = "Plant Reference",
            ["UiTabPopulation"] = "Population",
            ["UiTabSettings"] = "Settings",
            ["UiLabelName"] = "Name",
            ["UiLabelWaterType"] = "Water type",
            ["UiLabelStartedOn"] = "Start date",
            ["UiPlantCommonName"] = "Common name",
            ["UiPlantReferenceChoice"] = "Reference catalog",
            ["UiPlantScientificName"] = "Scientific name",
            ["UiPlantGrowth"] = "Growth",
            ["UiGrowthSlow"] = "Slow",
            ["UiGrowthMedium"] = "Medium",
            ["UiGrowthFast"] = "Fast",
            ["UiPlantLightNeed"] = "Light",
            ["UiLightLow"] = "Low",
            ["UiLightMedium"] = "Medium",
            ["UiLightHigh"] = "High",
            ["UiButtonAddMeasurement"] = "Add measurement",
            ["UiButtonDuplicateMeasurement"] = "Duplicate measurement",
            ["UiButtonDeleteMeasurement"] = "Delete measurement",
            ["UiButtonAddPlant"] = "Add plant",
            ["UiButtonDeletePlant"] = "Delete plant",
            ["UiGridScientific"] = "Scientific",
            ["UiGridGrowth"] = "Growth",
            ["UiPopulationSpecies"] = "Species",
            ["UiAnimalReferenceChoice"] = "Animal reference",
            ["UiPopulationType"] = "Type",
            ["UiPopulationQuantity"] = "Quantity",
            ["UiButtonAddPopulation"] = "Add population",
            ["UiButtonDeletePopulation"] = "Delete population",
            ["UiDbActionsHelp"] = "Database and maintenance actions.",
            ["UiButtonConfigureMySql"] = "Configure MySQL",
            ["UiButtonInitializeMySql"] = "Initialize MySQL",
            ["UiButtonSave"] = "Save",
            ["UiLabelLanguage"] = "Language",
            ["UiLabelTheme"] = "Theme",
            ["UiHealthCharts"] = "Trend charts",
            ["UiHealthPeriod"] = "Period",
            ["UiHealthPeriod7"] = "7 days",
            ["UiHealthPeriod30"] = "30 days",
            ["UiHealthPeriod90"] = "90 days",
            ["UiHealthPeriodAll"] = "Full history",
            ["UiHealthParameters"] = "Parameters to display",
            ["UiHealthNoChartData"] = "Not enough measurements to draw a chart.",
            ["UiLangFrench"] = "French",
            ["UiLangEnglish"] = "English",
            ["UiLangGerman"] = "German",
            ["UiThemeLight"] = "Light",
            ["UiThemeDark"] = "Dark",
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
            ["StatusConnectedNoAquarium"] = "MySQL connected. No aquariums in database yet.",
            ["StatusConnectedAquariumCount"] = "MySQL connected. {0} aquarium(s) loaded.",
            ["StatusLocalModeConfigure"] = "Local mode: configure MySQL to save to database.",
            ["StatusSaveInvalidInput"] = "Save failed: fix input errors before saving.",
            ["StatusAquariumSaved"] = "Aquarium saved to MySQL.",
            ["StatusSaveFailed"] = "MySQL save failed:",
            ["StatusNewAquariumSaved"] = "New aquarium saved.",
            ["StatusAquariumDeleted"] = "Aquarium deleted.",
            ["StatusAquariumDeleteFailed"] = "Aquarium deletion failed:",
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
            ["StatusLanguageChanged"] = "Language applied.",
            ["StatusThemeChanged"] = "Theme applied.",
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
            ["ConfirmDeleteAquarium"] = "Delete aquarium \"{0}\" and all related data?",
            ["ConfirmDeleteMeasurement"] = "Delete measurement from {0:g}?",
            ["ConfirmDeletePlant"] = "Delete plant \"{0}\"?",
            ["ConfirmDeletePopulation"] = "Delete \"{0}\" from population?",
            ["DefaultWaterType"] = "Freshwater",
            ["DefaultMainAquarium"] = "Main tank",
            ["DefaultMainAquariumNote"] = "First ADAqua aquarium.",
            ["DefaultPlantLight"] = "Low",
            ["DefaultNeonName"] = "Neon tetra"
        };

        var de = new Dictionary<string, string>(fr)
        {
            ["UiAppSubtitle"] = "Verwaltung von Aquarien, Wasserwerten, Pflanzen und Besatz",
            ["UiSectionAquariums"] = "Aquarien",
            ["UiButtonNewAquarium"] = "Neues Aquarium",
            ["UiButtonDeleteAquarium"] = "Aquarium loeschen",
            ["UiTabSheet"] = "Uebersicht",
            ["UiTabParameters"] = "Parameter",
            ["UiTabPlants"] = "Pflanzen",
            ["UiTabPlantReference"] = "Pflanzenkatalog",
            ["UiTabPopulation"] = "Besatz",
            ["UiTabSettings"] = "Einstellungen",
            ["UiLabelName"] = "Name",
            ["UiLabelWaterType"] = "Wassertyp",
            ["UiLabelStartedOn"] = "Startdatum",
            ["UiPlantCommonName"] = "Trivialname",
            ["UiPlantReferenceChoice"] = "Pflanzenkatalog",
            ["UiPlantScientificName"] = "Wissenschaftlicher Name",
            ["UiPlantGrowth"] = "Wachstum",
            ["UiGrowthSlow"] = "Langsam",
            ["UiGrowthMedium"] = "Mittel",
            ["UiGrowthFast"] = "Schnell",
            ["UiPlantLightNeed"] = "Licht",
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
            ["UiPopulationSpecies"] = "Art",
            ["UiAnimalReferenceChoice"] = "Tierkatalog",
            ["UiPopulationType"] = "Typ",
            ["UiPopulationQuantity"] = "Menge",
            ["UiButtonAddPopulation"] = "Besatz hinzufuegen",
            ["UiButtonDeletePopulation"] = "Besatz loeschen",
            ["UiDbActionsHelp"] = "Datenbank- und Wartungsaktionen.",
            ["UiButtonInitializeMySql"] = "MySQL initialisieren",
            ["UiButtonSave"] = "Speichern",
            ["UiLabelLanguage"] = "Sprache",
            ["UiLabelTheme"] = "Design",
            ["UiHealthCharts"] = "Trenddiagramme",
            ["UiHealthPeriod"] = "Zeitraum",
            ["UiHealthPeriod7"] = "7 Tage",
            ["UiHealthPeriod30"] = "30 Tage",
            ["UiHealthPeriod90"] = "90 Tage",
            ["UiHealthPeriodAll"] = "Gesamte Historie",
            ["UiHealthParameters"] = "Anzuzeigende Parameter",
            ["UiHealthNoChartData"] = "Nicht genug Messungen fuer ein Diagramm.",
            ["UiLangFrench"] = "Franzoesisch",
            ["UiLangEnglish"] = "Englisch",
            ["UiLangGerman"] = "Deutsch",
            ["UiThemeLight"] = "Hell",
            ["UiThemeDark"] = "Dunkel",
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
            ["StatusConnectedNoAquarium"] = "MySQL verbunden. Noch keine Aquarien in der Datenbank.",
            ["StatusConnectedAquariumCount"] = "MySQL verbunden. {0} Aquarium/Aquarien geladen.",
            ["StatusLocalModeConfigure"] = "Lokaler Modus: MySQL konfigurieren, um in die Datenbank zu speichern.",
            ["StatusSaveInvalidInput"] = "Speichern fehlgeschlagen: Eingabefehler zuerst korrigieren.",
            ["StatusAquariumSaved"] = "Aquarium in MySQL gespeichert.",
            ["StatusSaveFailed"] = "MySQL-Speichern fehlgeschlagen:",
            ["StatusNewAquariumSaved"] = "Neues Aquarium gespeichert.",
            ["StatusAquariumDeleted"] = "Aquarium geloescht.",
            ["StatusAquariumDeleteFailed"] = "Loeschen des Aquariums fehlgeschlagen:",
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
            ["StatusLanguageChanged"] = "Sprache angewendet.",
            ["StatusThemeChanged"] = "Design angewendet.",
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
            ["HealthActionWarningNitrates"] = "Teilwasserwechsel einplanen, um Nitrate zu senken.",
            ["HealthActionWarningPh"] = "pH pruefen und bei Bedarf schrittweise anpassen.",
            ["HealthActionWarningHardness"] = "GH/KH pruefen und Wechselwasser anpassen.",
            ["HealthActionWarningGeneric"] = "Entwicklung bei den naechsten Messungen beobachten.",
            ["HealthActionCriticalWaterChange"] = "Schnellen Wasserwechsel durchfuehren und Filterung/Belueftung pruefen.",
            ["HealthActionCriticalFeeding"] = "Fuetterung voruebergehend reduzieren, um Stickstofflast zu senken.",
            ["HealthActionCriticalTemperature"] = "Temperatur (Heizen/Kuehlen) ohne abrupte Schwankung korrigieren.",
            ["HealthActionCriticalGeneric"] = "Wasser analysieren und kritische Parameter zuerst stabilisieren.",
            ["ConfirmDeleteTitle"] = "Loeschbestaetigung",
            ["ConfirmDeleteAquarium"] = "Aquarium \"{0}\" und alle zugehoerigen Daten loeschen?",
            ["ConfirmDeleteMeasurement"] = "Messung vom {0:g} loeschen?",
            ["ConfirmDeletePlant"] = "Pflanze \"{0}\" loeschen?",
            ["ConfirmDeletePopulation"] = "\"{0}\" aus dem Besatz loeschen?",
            ["DefaultWaterType"] = "Suesswasser",
            ["DefaultMainAquarium"] = "Hauptbecken",
            ["DefaultMainAquariumNote"] = "Erstes ADAqua-Aquarium.",
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
    private sealed record HealthRule(string Name, Func<WaterParameters, decimal?> Selector, decimal CriticalMin, decimal WarningMin, decimal WarningMax, decimal CriticalMax);

    private Func<string, string> text = key => key;
    private string currentLanguage = "fr";
    private static readonly HealthRule[] HealthRules =
    [
        new("Amoniac", m => m.AmmoniaMgPerLiter, 0m, 0m, 0.05m, 0.2m),
        new("Nitrites", m => m.NitritesMgPerLiter, 0m, 0m, 0.02m, 0.1m),
        new("Nitrates", m => m.NitratesMgPerLiter, 0m, 0m, 25m, 40m),
        new("pH", m => m.Ph, 6m, 6.5m, 7.8m, 8.5m),
        new("GH", m => m.Gh, 1m, 4m, 12m, 20m),
        new("KH", m => m.Kh, 0m, 3m, 10m, 15m),
        new("Temperature", m => m.TemperatureCelsius, 18m, 22m, 27m, 30m)
    ];

    private Aquarium selectedAquarium;
    private WaterParameters? selectedMeasurement;
    private AquariumPlant? selectedPlant;
    private PopulationMember? selectedPopulation;
    private PlantReferenceItem? selectedPlantReferenceForNewPlant;
    private AnimalReferenceItem? selectedAnimalReferenceForNewPopulation;
    private PlantReferenceItem? selectedPlantReference;
    private AnimalReferenceItem? selectedAnimalReference;
    private string statusMessage = string.Empty;
    private string healthLastMeasurementAt = "-";
    private string healthGlobalStatus = "-";
    private string applicationLogText = string.Empty;
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
    private int selectedTrendPeriodDays = 30;
    private readonly List<PlantReferenceItem> plantReferenceCatalog = [];
    private readonly List<AnimalReferenceItem> animalReferenceCatalog = [];

    public MainWindowViewModel()
    {
        InitializeTrendParameterOptions();
        selectedAquarium = CreateDefaultAquarium();
        Aquariums.Add(selectedAquarium);
        RebuildHealthDashboard();
        RebuildPlantReference();
        RebuildAnimalReference();
        StatusMessage = text("StatusReady");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Aquarium> Aquariums { get; } = [];
    public WaterParameters NewMeasurement { get; private set; } = new();
    public AquariumPlant NewPlant { get; private set; } = new();
    public PopulationMember NewPopulation { get; private set; } = new();
    public ObservableCollection<HealthIndicator> HealthIndicators { get; } = [];
    public ObservableCollection<TrendParameterOption> TrendParameterOptions { get; } = [];
    public ObservableCollection<HealthTrendSeries> HealthTrendSeries { get; } = [];
    public ObservableCollection<string> HealthRecommendedActions { get; } = [];
    public ObservableCollection<PlantReferenceItem> PlantReferenceChoices { get; } = [];
    public ObservableCollection<PlantReferenceItem> PlantReferencesFiltered { get; } = [];
    public ObservableCollection<AnimalReferenceItem> AnimalReferenceChoices { get; } = [];
    public ObservableCollection<AnimalReferenceItem> AnimalReferencesFiltered { get; } = [];

    public Aquarium SelectedAquarium
    {
        get => selectedAquarium;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetField(ref selectedAquarium, value))
            {
                SelectedMeasurement = null;
                SelectedPlant = null;
                SelectedPopulation = null;
                SelectedPlantReferenceForNewPlant = null;
                SelectedAnimalReferenceForNewPopulation = null;
                OnPropertyChanged(nameof(StartedOnDateTime));
                RebuildHealthDashboard();
                RebuildPlantReference();
                RebuildAnimalReference();
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

    public DateTime StartedOnDateTime
    {
        get => SelectedAquarium.StartedOn.ToDateTime(TimeOnly.MinValue);
        set
        {
            SelectedAquarium.StartedOn = DateOnly.FromDateTime(value);
            OnPropertyChanged();
        }
    }

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
        if (string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = text("StatusReady");
        }
    }

    public void SetLanguage(string languageCode)
    {
        currentLanguage = languageCode is "en" or "de" ? languageCode : "fr";
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
    }

    public void NotifyLanguageChanged()
    {
        if (SelectedAquarium.WaterType is "Eau douce" or "Freshwater" or "Suesswasser")
        {
            SelectedAquarium.WaterType = text("DefaultWaterType");
            OnPropertyChanged(nameof(SelectedAquarium));
        }

        RebuildHealthDashboard();
        OnPropertyChanged(nameof(SelectedAquarium));
        RebuildPlantReferenceChoices();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    public void AddAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = $"Aquarium {Aquariums.Count + 1}",
            VolumeLiters = 60,
            WaterType = text("DefaultWaterType")
        };

        Aquariums.Add(aquarium);
        SelectedAquarium = aquarium;
        StatusMessage = text("StatusNewAquariumSaved");
        RebuildHealthDashboard();
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
            SortMeasurementsDescending(aquarium);
            Aquariums.Add(aquarium);
        }

        if (Aquariums.Count == 0)
        {
            AddAquarium();
            return;
        }

        SelectedAquarium = Aquariums[0];
        RebuildHealthDashboard();
        RebuildPlantReference();
        RebuildAnimalReference();
    }

    public void SelectAquarium(Guid aquariumId)
    {
        var aquarium = Aquariums.FirstOrDefault(candidate => candidate.Id == aquariumId);
        if (aquarium is not null)
        {
            SortMeasurementsDescending(aquarium);
            SelectedAquarium = aquarium;
            RebuildHealthDashboard();
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
        SelectedAquarium.Plants.Add(NewPlant);
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
        SelectedAquarium.Population.Add(NewPopulation);
        SelectedPopulation = NewPopulation;
        NewPopulation = new PopulationMember();
        SelectedAnimalReferenceForNewPopulation = null;
        OnPropertyChanged(nameof(NewPopulation));
        RefreshSelectedAquarium();
    }

    private void ApplyAnimalReferenceToNewPopulation(AnimalReferenceItem reference)
    {
        NewPopulation.CommonName = reference.LocalizedCommonName;
        NewPopulation.SpeciesName = reference.ScientificName;
        NewPopulation.Type = ResolvePopulationType(reference);
        OnPropertyChanged(nameof(NewPopulation));
    }

    private static PopulationType ResolvePopulationType(AnimalReferenceItem reference)
    {
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
            || searchText.Contains("snail", StringComparison.Ordinal)
            || searchText.Contains("schnecke", StringComparison.Ordinal)
            || searchText.Contains("neritina", StringComparison.Ordinal)
            || searchText.Contains("nerite", StringComparison.Ordinal)
            || searchText.Contains("pomacea", StringComparison.Ordinal)
            || searchText.Contains("planorbe", StringComparison.Ordinal))
        {
            return PopulationType.Snail;
        }

        return PopulationType.Fish;
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

    private void RefreshSelectedAquarium()
    {
        OnPropertyChanged(nameof(SelectedAquarium));
        RebuildHealthDashboard();
        RebuildPlantReference();
    }

    private Aquarium CreateDefaultAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = text("DefaultMainAquarium"),
            VolumeLiters = 120,
            WaterType = text("DefaultWaterType"),
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
        HealthLastMeasurementAt = latest.MeasuredAt.ToString("g", CultureInfo.CurrentCulture);

        var hasCritical = false;
        var hasWarning = false;
        foreach (var rule in HealthRules)
        {
            var latestValue = rule.Selector(latest);
            var previousValue = previous is null ? null : rule.Selector(previous);
            var trend = BuildTrend(latestValue, previousValue);
            var alert = BuildAlert(latestValue, rule);

            hasCritical |= alert == text("HealthAlertCritical");
            hasWarning |= alert == text("HealthAlertWarning");

            HealthIndicators.Add(new HealthIndicator(
                rule.Name,
                latestValue?.ToString("0.##") ?? "-",
                trend,
                alert));
        }

        RebuildHealthTrendSeries(aquarium);

        if (hasCritical)
        {
            HealthGlobalStatus = text("HealthStatusCritical");
            AddActionsForCritical(latest);
            return;
        }

        if (hasWarning)
        {
            HealthGlobalStatus = text("HealthStatusWarning");
            AddActionsForWarning(latest);
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

        AddTrendSeries("Amoniac", "mg/L", filtered, m => m.AmmoniaMgPerLiter);
        AddTrendSeries("Nitrites", "mg/L", filtered, m => m.NitritesMgPerLiter);
        AddTrendSeries("Nitrates", "mg/L", filtered, m => m.NitratesMgPerLiter);
        AddTrendSeries("pH", string.Empty, filtered, m => m.Ph);
        AddTrendSeries("GH", string.Empty, filtered, m => m.Gh);
        AddTrendSeries("KH", string.Empty, filtered, m => m.Kh);
        AddTrendSeries("Temperature", "C", filtered, m => m.TemperatureCelsius);
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

    private void AddTrendSeries(string name, string unit, IReadOnlyList<WaterParameters> measurements, Func<WaterParameters, decimal?> selector)
    {
        if (!TrendParameterOptions.Any(option => option.IsSelected && string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)))
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

        HealthTrendSeries.Add(new HealthTrendSeries(name, latestLabel, points, yTopLabel, yMidLabel, yBottomLabel, xStartLabel, xMidLabel, xEndLabel));
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
            PlantReferenceEnvironmentLabel = "Referentiel plantes - type inconnu";
            return;
        }

        var environment = ResolveEnvironmentType(selectedAquarium.WaterType);
        PlantReferenceEnvironmentLabel = environment == PlantReferenceEnvironment.Marine
            ? "Referentiel plantes - Eau de mer"
            : "Referentiel plantes - Eau douce tropicale";

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
            AnimalReferenceEnvironmentLabel = "Referentiel population - type inconnu";
            return;
        }

        var environment = ResolveAnimalEnvironmentType(selectedAquarium.WaterType);
        AnimalReferenceEnvironmentLabel = environment == AnimalReferenceEnvironment.Marine
            ? "Referentiel population - Eau de mer"
            : "Referentiel population - Eau douce tropicale";

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
        foreach (var item in animalReferenceCatalog
            .Where(item => item.Environment == environment)
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
        RebuildAnimalReference();
    }

    private bool MatchesAnimalFilters(AnimalReferenceItem item)
    {
        return MatchesMin(item.PhMin, AnimalFilterPhMin)
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

    private static PlantReferenceEnvironment ResolveEnvironmentType(string? waterType)
    {
        if (string.IsNullOrWhiteSpace(waterType))
        {
            return PlantReferenceEnvironment.FreshwaterTropical;
        }

        var normalized = waterType.Trim().ToLowerInvariant();
        return normalized.Contains("mer") || normalized.Contains("sea") || normalized.Contains("marine")
            ? PlantReferenceEnvironment.Marine
            : PlantReferenceEnvironment.FreshwaterTropical;
    }

    private static AnimalReferenceEnvironment ResolveAnimalEnvironmentType(string? waterType)
    {
        if (string.IsNullOrWhiteSpace(waterType))
        {
            return AnimalReferenceEnvironment.FreshwaterTropical;
        }

        var normalized = waterType.Trim().ToLowerInvariant();
        return normalized.Contains("mer") || normalized.Contains("sea") || normalized.Contains("marine")
            ? AnimalReferenceEnvironment.Marine
            : AnimalReferenceEnvironment.FreshwaterTropical;
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
        AddTrendOption("Amoniac");
        AddTrendOption("Nitrites");
        AddTrendOption("Nitrates");
        AddTrendOption("pH");
        AddTrendOption("GH");
        AddTrendOption("KH");
        AddTrendOption("Temperature");
    }

    private void AddTrendOption(string name)
    {
        var option = new TrendParameterOption(name, true);
        option.PropertyChanged += OnTrendParameterOptionChanged;
        TrendParameterOptions.Add(option);
    }

    private void OnTrendParameterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrendParameterOption.IsSelected))
        {
            RebuildHealthDashboard();
        }
    }

    private static string BuildTrend(decimal? latest, decimal? previous)
    {
        if (latest is null || previous is null)
        {
            return "N/A";
        }

        if (latest > previous)
        {
            return "Hausse";
        }

        if (latest < previous)
        {
            return "Baisse";
        }

        return "Stable";
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

    private void AddActionsForCritical(WaterParameters latest)
    {
        if ((latest.AmmoniaMgPerLiter ?? 0m) > 0.2m || (latest.NitritesMgPerLiter ?? 0m) > 0.1m)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalWaterChange"));
            HealthRecommendedActions.Add(text("HealthActionCriticalFeeding"));
        }

        if ((latest.TemperatureCelsius ?? 24m) < 18m || (latest.TemperatureCelsius ?? 24m) > 30m)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalTemperature"));
        }

        if (HealthRecommendedActions.Count == 0)
        {
            HealthRecommendedActions.Add(text("HealthActionCriticalGeneric"));
        }
    }

    private void AddActionsForWarning(WaterParameters latest)
    {
        if ((latest.NitratesMgPerLiter ?? 0m) > 25m)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningNitrates"));
        }

        if ((latest.Ph ?? 7m) < 6.5m || (latest.Ph ?? 7m) > 7.8m)
        {
            HealthRecommendedActions.Add(text("HealthActionWarningPh"));
        }

        if ((latest.Gh ?? 8m) < 4m || (latest.Gh ?? 8m) > 12m || (latest.Kh ?? 5m) < 3m || (latest.Kh ?? 5m) > 10m)
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

public sealed record HealthIndicator(string Name, string LatestDisplay, string Trend, string Alert);
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

    public string EnvironmentLabel => Environment == PlantReferenceEnvironment.Marine ? "Eau de mer" : "Eau douce tropicale";

    public string LocalizedCommonName => currentLanguage switch
    {
        "en" => FirstNonEmpty(CommonNameEn, CommonName, CommonNameFr, CommonNameDe, ScientificName),
        "de" => FirstNonEmpty(CommonNameDe, CommonName, CommonNameFr, CommonNameEn, ScientificName),
        _ => FirstNonEmpty(CommonNameFr, CommonName, CommonNameEn, CommonNameDe, ScientificName)
    };

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode is "en" or "de" ? languageCode : "fr";
        if (currentLanguage == normalized)
        {
            return;
        }

        currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCommonName)));
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

    public string EnvironmentLabel => Environment == AnimalReferenceEnvironment.Marine ? "Eau de mer" : "Eau douce tropicale";

    public string LocalizedCommonName => currentLanguage switch
    {
        "en" => FirstNonEmpty(CommonNameEn, CommonName, CommonNameFr, CommonNameDe, ScientificName),
        "de" => FirstNonEmpty(CommonNameDe, CommonName, CommonNameFr, CommonNameEn, ScientificName),
        _ => FirstNonEmpty(CommonNameFr, CommonName, CommonNameEn, CommonNameDe, ScientificName)
    };

    public void SetLanguage(string languageCode)
    {
        var normalized = languageCode is "en" or "de" ? languageCode : "fr";
        if (currentLanguage == normalized)
        {
            return;
        }

        currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalizedCommonName)));
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

public sealed class TrendParameterOption : INotifyPropertyChanged
{
    private bool isSelected;

    public TrendParameterOption(string name, bool isSelected)
    {
        Name = name;
        this.isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

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
}
