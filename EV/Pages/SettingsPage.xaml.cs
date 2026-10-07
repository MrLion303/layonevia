using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class SettingsPage : UserControl
{
    private readonly AiSettingsStore _aiSettings = new();

    public SettingsPage()
    {
        InitializeComponent();
        LoadAiSettings();
    }

    private void LoadAiSettings()
    {
        try
        {
            var settings = _aiSettings.Load();
            ModelBox.Text = settings.Model;
            EndpointBox.Text = settings.Endpoint;
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
