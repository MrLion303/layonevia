using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class SettingsPage : UserControl
{
    public event EventHandler? SettingsChanged;
    private readonly AiSettingsStore _aiSettings = new();
    private readonly AppSettingsStore _appSettings = new();
    private readonly WindowsStartupService _startup = new();
    private bool _loadingGeneral;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private bool _loaded;

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        LoadGeneralSettings();
        LoadAiSettings();
    }

    private void LoadGeneralSettings()
    {
        try
        {
            _loadingGeneral = true;
            var settings = _appSettings.Load();
            StartWithWindowsBox.IsChecked = settings.StartWithWindows;
            VoiceResponseBox.IsChecked = settings.VoiceResponseEnabled;
            WakeWordBox.IsChecked = settings.WakeWordEnabled;
            MinimizeToTrayBox.IsChecked = settings.MinimizeToTray;
        }
        finally
        {
            _loadingGeneral = false;
        }
    }

    private void GeneralSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral || !_loaded)
            return;

        try
        {
            var settings = _appSettings.Load();
            settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
            settings.VoiceResponseEnabled = VoiceResponseBox.IsChecked == true;
            settings.WakeWordEnabled = WakeWordBox.IsChecked == true;
            settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
            _appSettings.Save(settings);
            _startup.SetEnabled(settings.StartWithWindows);
            AiStatus.Text = "Ajustes generales guardados.";
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron guardar los ajustes generales");
            AiStatus.Text = "No se pudieron guardar los ajustes generales.";
        }
    }

    private void LoadAiSettings()
    {
        try
        {
            var settings = _aiSettings.Load();
            ModelBox.Text = settings.Model ?? "gpt-6-luna";
            EndpointBox.Text = settings.Endpoint ?? "https://api.openai.com/v1/responses";
            AiStatus.Text = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? "Inteligencia no configurada."
                : "Inteligencia configurada.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar los ajustes de inteligencia");
            AiStatus.Text = "No se pudieron cargar los ajustes.";
        }
    }

    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AiStatus.Text = "Probando conexión...";
            var ai = new ConversationAiService();
            if (!ai.IsConfigured)
            {
                AiStatus.Text = "No hay una clave configurada.";
                return;
            }

            var result = await ai.RespondAsync("Responde únicamente: conexión correcta.");
            AiStatus.Text = result.Succeeded
                ? $"Conexión correcta: {result.Text}"
                : result.Text;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo probar la conexión de IA");
            AiStatus.Text = "No se pudo comprobar la conexión.";
        }
    }

    private void ClearAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = MessageBox.Show(
                "¿Quieres borrar la clave de API guardada en este equipo?",
                "EV",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var current = _aiSettings.Load();
            _aiSettings.Save(new AiSettings
            {
                ApiKey = null,
                Model = current.Model,
                Endpoint = current.Endpoint ?? "https://api.openai.com/v1/responses"
            });

            ApiKeyBox.Clear();
            AiStatus.Text = "Clave eliminada.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo borrar la clave de IA");
            AiStatus.Text = "No se pudo borrar la clave.";
        }
    }

    private void SaveAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = _aiSettings.Load();
            var key = ApiKeyBox.Password.Trim();

            var settings = new AiSettings
            {
                ApiKey = key.Length > 0 ? key : current.ApiKey,
                Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "gpt-6-luna" : ModelBox.Text.Trim(),
                Endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text)
                    ? "https://api.openai.com/v1/responses"
                    : EndpointBox.Text.Trim()
            };

            _aiSettings.Save(settings);
            ApiKeyBox.Clear();
            AiStatus.Text = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? "Inteligencia no configurada."
                : "Configuración guardada.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron guardar los ajustes de inteligencia");
            AiStatus.Text = "No se pudo guardar la configuración.";
        }
    }
}