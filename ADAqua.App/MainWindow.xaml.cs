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
    private readonly MySqlAquariumRepository? repository;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;

        var connectionString = ResolveConnectionString();
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            repository = new MySqlAquariumRepository(connectionString);
            viewModel.StatusMessage = "MySQL configure via ADAQUA_MYSQL_CONNECTION_STRING.";
        }
        else
        {
            viewModel.StatusMessage = "Variable ADAQUA_MYSQL_CONNECTION_STRING introuvable. Redemarre Visual Studio si tu viens de la creer.";
        }
    }

    private static string? ResolveConnectionString()
    {
        return Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ADAQUA_MYSQL_CONNECTION_STRING", EnvironmentVariableTarget.Machine);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            return;
        }

        try
        {
            var aquariums = await repository.GetAllAsync();
            viewModel.ReplaceAquariums(aquariums);
            viewModel.StatusMessage = aquariums.Count == 0
                ? "MySQL connecte. Aucun aquarium en base pour le moment."
                : $"MySQL connecte. {aquariums.Count} aquarium(s) charge(s).";
        }
        catch (Exception exception)
        {
            viewModel.StatusMessage = $"Lecture MySQL indisponible: {exception.Message}";
        }
    }

    private async void InitializeDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Definis ADAQUA_MYSQL_CONNECTION_STRING pour activer MySQL.";
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null)
        {
            viewModel.StatusMessage = "Mode local: aucune chaine de connexion MySQL n'est configuree.";
            return;
        }

        var store = new ResilientAquariumStore(repository);
        var result = await store.SaveAsync(viewModel.SelectedAquarium);
        viewModel.StatusMessage = result.Message;
    }

    private void NewAquarium_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddAquarium();
    }

    private void AddMeasurement_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddMeasurement();
    }

    private void AddPlant_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPlant();
    }

    private void AddPopulation_Click(object sender, RoutedEventArgs e)
    {
        viewModel.AddPopulation();
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private Aquarium selectedAquarium;
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
        set => SetField(ref selectedAquarium, value);
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

    public void AddMeasurement()
    {
        NewMeasurement.MeasuredAt = DateTime.Now;
        SelectedAquarium.Measurements.Insert(0, NewMeasurement);
        NewMeasurement = new WaterParameters();
        OnPropertyChanged(nameof(NewMeasurement));
        RefreshSelectedAquarium();
        StatusMessage = "Mesure d'eau ajoutee.";
    }

    public void AddPlant()
    {
        SelectedAquarium.Plants.Add(NewPlant);
        NewPlant = new AquariumPlant();
        OnPropertyChanged(nameof(NewPlant));
        RefreshSelectedAquarium();
        StatusMessage = "Plante ajoutee.";
    }

    public void AddPopulation()
    {
        SelectedAquarium.Population.Add(NewPopulation);
        NewPopulation = new PopulationMember();
        OnPropertyChanged(nameof(NewPopulation));
        RefreshSelectedAquarium();
        StatusMessage = "Population ajoutee.";
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
