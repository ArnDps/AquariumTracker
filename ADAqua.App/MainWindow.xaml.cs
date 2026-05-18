using ADAqua.Domain;
using ADAqua.Infrastructure;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ADAqua.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel = new();
    private MySqlAquariumRepository? repository;
    private string? activeConnectionString;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;

        var resolved = ResolveConnectionConfiguration();
        if (!string.IsNullOrWhiteSpace(resolved.ConnectionString))
        {
            ApplyConnectionString(resolved.ConnectionString, resolved.Message);
        }
        else
        {
            viewModel.StatusMessage = "MySQL non configure. Utilise Configurer MySQL pour enregistrer une connexion locale.";
        }
    }

    private static (string? ConnectionString, string Message) ResolveConnectionConfiguration()
    {
        var savedSettings = MySqlConfigurationStore.Load();
        if (savedSettings is not null)
        {
            return (savedSettings.BuildConnectionString(), "MySQL configure depuis la configuration locale securisee.");
        }

        var environmentConnectionString = Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Machine);

        return string.IsNullOrWhiteSpace(environmentConnectionString)
            ? (null, string.Empty)
            : (environmentConnectionString, "MySQL configure via ADAQUA_MYSQL_CONNECTION_STRING.");
    }

    private void ApplyConnectionString(string connectionString, string message)
    {
        activeConnectionString = connectionString;
        repository = new MySqlAquariumRepository(connectionString);
        viewModel.StatusMessage = message;
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
            viewModel.StatusMessage = $"Lecture MySQL indisponible: {exception.Message}";
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
            ApplyConnectionString(configurationWindow.ConnectionString, "Configuration MySQL enregistree et active.");
            try
            {
                await LoadAquariumsAsync();
            }
            catch (Exception exception)
            {
                viewModel.StatusMessage = $"Configuration enregistree, mais lecture MySQL impossible: {exception.Message}";
            }
        }
    }

    private async void InitializeDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Configure MySQL avant d'initialiser le schema.";
            return;
        }

        try
        {
            await repository.InitializeAsync();
            viewModel.StatusMessage = "Schema MySQL initialise.";
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"Initialisation MySQL impossible: {exception.Message}";
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
            ? "MySQL connecte. Aucun aquarium en base pour le moment."
            : $"MySQL connecte. {aquariums.Count} aquarium(s) charge(s).";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Mode local: configure MySQL pour sauvegarder en base.";
            return;
        }

        try
        {
            var aquariumId = viewModel.SelectedAquarium.Id;
            await repository.InitializeAsync();
            await repository.SaveAsync(viewModel.SelectedAquarium);
            await LoadAquariumsAsync(aquariumId);
            viewModel.StatusMessage = "Aquarium sauvegarde dans MySQL.";
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"Sauvegarde MySQL impossible: {exception.Message}";
        }
    }

    private void NewAquarium_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddAquarium();
    }

    private async void DeleteAquarium_Click(object sender, RoutedEventArgs e)
    {
        var aquarium = viewModel.SelectedAquarium;
        var result = MessageBox.Show(
            $"Supprimer l'aquarium \"{aquarium.Name}\" et toutes ses donnees associees ?",
            "Confirmation de suppression",
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
            viewModel.StatusMessage = "Aquarium supprime.";
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"Suppression de l'aquarium impossible: {exception.Message}";
        }
    }

    private void AddMeasurement_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddMeasurement();
    }

    private void DeleteMeasurement_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedMeasurement is null)
        {
            viewModel.StatusMessage = "Selectionne une mesure a supprimer.";
            return;
        }

        var result = MessageBox.Show(
            $"Supprimer la mesure du {viewModel.SelectedMeasurement.MeasuredAt:g} ?",
            "Confirmation de suppression",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedMeasurement();
        }
    }

    private void AddPlant_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPlant();
    }

    private void DeletePlant_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPlant is null)
        {
            viewModel.StatusMessage = "Selectionne une plante a supprimer.";
            return;
        }

        var result = MessageBox.Show(
            $"Supprimer la plante \"{viewModel.SelectedPlant.CommonName}\" ?",
            "Confirmation de suppression",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedPlant();
        }
    }

    private void AddPopulation_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPopulation();
    }

    private void DeletePopulation_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPopulation is null)
        {
            viewModel.StatusMessage = "Selectionne une population a supprimer.";
            return;
        }

        var result = MessageBox.Show(
            $"Supprimer \"{viewModel.SelectedPopulation.CommonName}\" de la population ?",
            "Confirmation de suppression",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteSelectedPopulation();
        }
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private Aquarium selectedAquarium;
    private WaterParameters? selectedMeasurement;
    private AquariumPlant? selectedPlant;
    private PopulationMember? selectedPopulation;
    private string statusMessage = "Pret. MySQL est optionnel au demarrage pour garder l'application utilisable hors ligne.";

    public MainWindowViewModel()
    {
        selectedAquarium = CreateDefaultAquarium();
        Aquariums.Add(selectedAquarium);
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

    public void AddAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = $"Aquarium {Aquariums.Count + 1}",
            VolumeLiters = 60,
            WaterType = "Eau douce"
        };

        Aquariums.Add(aquarium);
        SelectedAquarium = aquarium;
        StatusMessage = "Nouvel aquarium ajoute.";
    }

    public void DeleteSelectedAquarium()
    {
        var aquarium = SelectedAquarium;
        var index = Aquariums.IndexOf(aquarium);
        Aquariums.Remove(aquarium);

        if (Aquariums.Count == 0)
        {
            AddAquarium();
            StatusMessage = "Aquarium supprime. Un nouvel aquarium local a ete prepare.";
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
        StatusMessage = "Mesure d'eau ajoutee.";
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
        StatusMessage = "Mesure d'eau supprimee. Clique sur Sauvegarder pour persister la modification.";
    }

    public void AddPlant()
    {
        SelectedAquarium.Plants.Add(NewPlant);
        SelectedPlant = NewPlant;
        NewPlant = new AquariumPlant();
        OnPropertyChanged(nameof(NewPlant));
        RefreshSelectedAquarium();
        StatusMessage = "Plante ajoutee.";
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
        StatusMessage = "Plante supprimee. Clique sur Sauvegarder pour persister la modification.";
    }

    public void AddPopulation()
    {
        SelectedAquarium.Population.Add(NewPopulation);
        SelectedPopulation = NewPopulation;
        NewPopulation = new PopulationMember();
        OnPropertyChanged(nameof(NewPopulation));
        RefreshSelectedAquarium();
        StatusMessage = "Population ajoutee.";
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
        StatusMessage = "Population supprimee. Clique sur Sauvegarder pour persister la modification.";
    }

    private void RefreshSelectedAquarium()
    {
        OnPropertyChanged(nameof(SelectedAquarium));
    }

    private static Aquarium CreateDefaultAquarium()
    {
        var aquarium = new Aquarium
        {
            Name = "Bac principal",
            VolumeLiters = 120,
            WaterType = "Eau douce",
            Notes = "Premier aquarium ADAqua."
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
        aquarium.Plants.Add(new AquariumPlant { CommonName = "Anubias", ScientificName = "Anubias barteri", LightNeed = "Faible" });
        aquarium.Population.Add(new PopulationMember { CommonName = "Neon bleu", SpeciesName = "Paracheirodon innesi", Quantity = 10 });

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
