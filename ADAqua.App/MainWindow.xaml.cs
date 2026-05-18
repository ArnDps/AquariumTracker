using ADAqua.Domain;
using ADAqua.Infrastructure;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    private string currentLanguage = LanguageFrench;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetTextProvider(T);

        ApplyTheme(ThemeLight);
        ApplyLanguage(LanguageFrench);

        isApplyingSettings = true;
        LanguageComboBox.SelectedIndex = 0;
        ThemeComboBox.SelectedIndex = 0;
        isApplyingSettings = false;

        var resolved = ResolveConnectionConfiguration();
        if (!string.IsNullOrWhiteSpace(resolved.ConnectionString))
        {
            ApplyConnectionString(resolved.ConnectionString, resolved.MessageKey);
        }
        else
        {
            viewModel.StatusMessage = T("StatusMySqlNotConfigured");
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
            await LoadAquariumsAsync();
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"{T("StatusReadFailed")} {exception.Message}";
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
        }
    }

    private async Task LoadAquariumsAsync(Guid? selectedAquariumId = null)
    {
        if (repository is null)
        {
            return;
        }

        var aquariums = await repository.GetAllAsync();
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
        }
    }

    private async void AddMeasurement_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddMeasurement();
        await PersistSelectedAquariumAsync(T("StatusMeasurementSaved"));
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

    private void ApplyLanguage(string languageCode)
    {
        if (!localizedTexts.TryGetValue(languageCode, out var texts))
        {
            texts = localizedTexts[LanguageFrench];
            languageCode = LanguageFrench;
        }

        currentLanguage = languageCode;
        foreach (var pair in texts)
        {
            Resources[pair.Key] = pair.Value;
        }

        viewModel.NotifyLanguageChanged();
    }

    private void ApplyTheme(string themeCode)
    {
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
            ["UiButtonDeleteMeasurement"] = "Supprimer la mesure",
            ["UiGridDate"] = "Date",
            ["UiPlantCommonName"] = "Nom courant",
            ["UiPlantScientificName"] = "Nom scientifique",
            ["UiPlantLightNeed"] = "Lumiere",
            ["UiButtonAddPlant"] = "Ajouter la plante",
            ["UiButtonDeletePlant"] = "Supprimer la plante",
            ["UiGridScientific"] = "Scientifique",
            ["UiGridGrowth"] = "Croissance",
            ["UiPopulationSpecies"] = "Espece",
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
            ["UiLangFrench"] = "Francais",
            ["UiLangEnglish"] = "Anglais",
            ["UiLangGerman"] = "Allemand",
            ["UiThemeLight"] = "Clair",
            ["UiThemeDark"] = "Sombre",
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
            ["StatusMeasurementSaved"] = "Mesure d'eau enregistree.",
            ["StatusMeasurementDeleted"] = "Mesure d'eau supprimee.",
            ["StatusSelectPlantDelete"] = "Selectionne une plante a supprimer.",
            ["StatusPlantSaved"] = "Plante enregistree.",
            ["StatusPlantDeleted"] = "Plante supprimee.",
            ["StatusSelectPopulationDelete"] = "Selectionne une population a supprimer.",
            ["StatusPopulationSaved"] = "Population enregistree.",
            ["StatusPopulationDeleted"] = "Population supprimee.",
            ["StatusLanguageChanged"] = "Langue appliquee.",
            ["StatusThemeChanged"] = "Theme applique.",
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
            ["UiTabPopulation"] = "Population",
            ["UiTabSettings"] = "Settings",
            ["UiLabelName"] = "Name",
            ["UiLabelWaterType"] = "Water type",
            ["UiLabelStartedOn"] = "Start date",
            ["UiPlantCommonName"] = "Common name",
            ["UiPlantScientificName"] = "Scientific name",
            ["UiPlantLightNeed"] = "Light",
            ["UiButtonAddMeasurement"] = "Add measurement",
            ["UiButtonDeleteMeasurement"] = "Delete measurement",
            ["UiButtonAddPlant"] = "Add plant",
            ["UiButtonDeletePlant"] = "Delete plant",
            ["UiGridScientific"] = "Scientific",
            ["UiGridGrowth"] = "Growth",
            ["UiPopulationSpecies"] = "Species",
            ["UiPopulationQuantity"] = "Quantity",
            ["UiButtonAddPopulation"] = "Add population",
            ["UiButtonDeletePopulation"] = "Delete population",
            ["UiDbActionsHelp"] = "Database and maintenance actions.",
            ["UiButtonConfigureMySql"] = "Configure MySQL",
            ["UiButtonInitializeMySql"] = "Initialize MySQL",
            ["UiButtonSave"] = "Save",
            ["UiLabelLanguage"] = "Language",
            ["UiLabelTheme"] = "Theme",
            ["UiLangFrench"] = "French",
            ["UiLangEnglish"] = "English",
            ["UiLangGerman"] = "German",
            ["UiThemeLight"] = "Light",
            ["UiThemeDark"] = "Dark",
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
            ["StatusMeasurementSaved"] = "Measurement saved.",
            ["StatusMeasurementDeleted"] = "Measurement deleted.",
            ["StatusSelectPlantDelete"] = "Select a plant to delete.",
            ["StatusPlantSaved"] = "Plant saved.",
            ["StatusPlantDeleted"] = "Plant deleted.",
            ["StatusSelectPopulationDelete"] = "Select a population entry to delete.",
            ["StatusPopulationSaved"] = "Population saved.",
            ["StatusPopulationDeleted"] = "Population deleted.",
            ["StatusLanguageChanged"] = "Language applied.",
            ["StatusThemeChanged"] = "Theme applied.",
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
            ["UiTabPopulation"] = "Besatz",
            ["UiTabSettings"] = "Einstellungen",
            ["UiLabelName"] = "Name",
            ["UiLabelWaterType"] = "Wassertyp",
            ["UiLabelStartedOn"] = "Startdatum",
            ["UiPlantCommonName"] = "Trivialname",
            ["UiPlantScientificName"] = "Wissenschaftlicher Name",
            ["UiPlantLightNeed"] = "Licht",
            ["UiButtonAddMeasurement"] = "Messung hinzufuegen",
            ["UiButtonDeleteMeasurement"] = "Messung loeschen",
            ["UiButtonAddPlant"] = "Pflanze hinzufuegen",
            ["UiButtonDeletePlant"] = "Pflanze loeschen",
            ["UiPopulationSpecies"] = "Art",
            ["UiPopulationQuantity"] = "Menge",
            ["UiButtonAddPopulation"] = "Besatz hinzufuegen",
            ["UiButtonDeletePopulation"] = "Besatz loeschen",
            ["UiDbActionsHelp"] = "Datenbank- und Wartungsaktionen.",
            ["UiButtonInitializeMySql"] = "MySQL initialisieren",
            ["UiButtonSave"] = "Speichern",
            ["UiLabelLanguage"] = "Sprache",
            ["UiLabelTheme"] = "Design",
            ["UiLangFrench"] = "Franzoesisch",
            ["UiLangEnglish"] = "Englisch",
            ["UiLangGerman"] = "Deutsch",
            ["UiThemeLight"] = "Hell",
            ["UiThemeDark"] = "Dunkel",
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
            ["StatusMeasurementSaved"] = "Messung gespeichert.",
            ["StatusMeasurementDeleted"] = "Messung geloescht.",
            ["StatusSelectPlantDelete"] = "Waehle eine Pflanze zum Loeschen aus.",
            ["StatusPlantSaved"] = "Pflanze gespeichert.",
            ["StatusPlantDeleted"] = "Pflanze geloescht.",
            ["StatusSelectPopulationDelete"] = "Waehle einen Besatzeintrag zum Loeschen aus.",
            ["StatusPopulationSaved"] = "Besatz gespeichert.",
            ["StatusPopulationDeleted"] = "Besatz geloescht.",
            ["StatusLanguageChanged"] = "Sprache angewendet.",
            ["StatusThemeChanged"] = "Design angewendet.",
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
    private Func<string, string> text = key => key;
    private Aquarium selectedAquarium;
    private WaterParameters? selectedMeasurement;
    private AquariumPlant? selectedPlant;
    private PopulationMember? selectedPopulation;
    private string statusMessage = string.Empty;

    public MainWindowViewModel()
    {
        selectedAquarium = CreateDefaultAquarium();
        Aquariums.Add(selectedAquarium);
        StatusMessage = text("StatusReady");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Aquarium> Aquariums { get; } = [];
    public WaterParameters NewMeasurement { get; private set; } = new();
    public AquariumPlant NewPlant { get; private set; } = new();
    public PopulationMember NewPopulation { get; private set; } = new();

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
                OnPropertyChanged(nameof(StartedOnDateTime));
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

    public void SetTextProvider(Func<string, string> provider)
    {
        text = provider;
        if (string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = text("StatusReady");
        }
    }

    public void NotifyLanguageChanged()
    {
        if (SelectedAquarium.WaterType is "Eau douce" or "Freshwater" or "Suesswasser")
        {
            SelectedAquarium.WaterType = text("DefaultWaterType");
            OnPropertyChanged(nameof(SelectedAquarium));
        }
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
    }

    public void ReplaceAquariums(IReadOnlyList<Aquarium> aquariums)
    {
        Aquariums.Clear();
        foreach (var aquarium in aquariums)
        {
            Aquariums.Add(aquarium);
        }

        if (Aquariums.Count == 0)
        {
            AddAquarium();
            return;
        }

        SelectedAquarium = Aquariums[0];
    }

    public void SelectAquarium(Guid aquariumId)
    {
        var aquarium = Aquariums.FirstOrDefault(candidate => candidate.Id == aquariumId);
        if (aquarium is not null)
        {
            SelectedAquarium = aquarium;
        }
    }

    public void AddMeasurement()
    {
        NewMeasurement.MeasuredAt = DateTime.Now;
        SelectedAquarium.Measurements.Insert(0, NewMeasurement);
        SelectedMeasurement = NewMeasurement;
        NewMeasurement = new WaterParameters();
        OnPropertyChanged(nameof(NewMeasurement));
        RefreshSelectedAquarium();
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
    }

    public void AddPlant()
    {
        SelectedAquarium.Plants.Add(NewPlant);
        SelectedPlant = NewPlant;
        NewPlant = new AquariumPlant();
        OnPropertyChanged(nameof(NewPlant));
        RefreshSelectedAquarium();
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
        OnPropertyChanged(nameof(NewPopulation));
        RefreshSelectedAquarium();
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
