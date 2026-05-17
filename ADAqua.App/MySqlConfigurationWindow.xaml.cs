using MySqlConnector;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ADAqua.App;

public partial class MySqlConfigurationWindow : Window
{
    private readonly MySqlConfigurationViewModel viewModel;

    public MySqlConfigurationWindow(MySqlConnectionSettings settings)
    {
        InitializeComponent();
        viewModel = new MySqlConfigurationViewModel(settings);
        DataContext = viewModel;
        PasswordInput.Password = settings.Password;
    }

    public string? ConnectionString { get; private set; }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        await TestConnectionAsync(showSuccess: true);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!await TestConnectionAsync(showSuccess: false))
        {
            return;
        }

        try
        {
            MySqlConfigurationStore.Save(viewModel.Settings);
            ConnectionString = viewModel.Settings.BuildConnectionString();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            viewModel.SetError($"Sauvegarde impossible: {exception.Message}");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        viewModel.Settings.Password = PasswordInput.Password;
    }

    private async Task<bool> TestConnectionAsync(bool showSuccess)
    {
        try
        {
            await using var connection = new MySqlConnection(viewModel.Settings.BuildConnectionString());
            await connection.OpenAsync();
            if (showSuccess)
            {
                viewModel.SetSuccess("Connexion MySQL reussie.");
            }

            return true;
        }
        catch (Exception exception)
        {
            viewModel.SetError($"Connexion MySQL impossible: {exception.Message}");
            return false;
        }
    }
}

public sealed class MySqlConfigurationViewModel : INotifyPropertyChanged
{
    private string statusMessage = "Renseigne la connexion MySQL, puis teste-la avant de l'enregistrer.";
    private Brush statusBrush = new SolidColorBrush(Color.FromRgb(83, 105, 110));

    public MySqlConfigurationViewModel(MySqlConnectionSettings settings)
    {
        Settings = settings;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MySqlConnectionSettings Settings { get; }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public Brush StatusBrush
    {
        get => statusBrush;
        private set => SetField(ref statusBrush, value);
    }

    public void SetSuccess(string message)
    {
        StatusBrush = new SolidColorBrush(Color.FromRgb(21, 107, 111));
        StatusMessage = message;
    }

    public void SetError(string message)
    {
        StatusBrush = new SolidColorBrush(Color.FromRgb(154, 52, 18));
        StatusMessage = message;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
