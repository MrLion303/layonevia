using System.Windows;
using System.Windows.Media;
using EV.Pages;
using EV.Services;

namespace EV;

public partial class MainWindow : Window
{
    private readonly VoiceRecognitionService _voiceRecognition = new();
    private readonly VoiceOutputService _voiceOutput = new();

    private HomePage? _home;
    private AudioPage? _audio;
    private MemoryPage? _memory;
    private SettingsPage? _settings;

    public MainWindow()
    {
        InitializeComponent();

        _voiceRecognition.StatusChanged += VoiceRecognition_StatusChanged;
        _voiceRecognition.CommandRecognized += VoiceRecognition_CommandRecognized;

        ShowPage("Inicio", GetHome());
        _voiceRecognition.Start();
    }

    private HomePage GetHome() => _home ??= new HomePage();
    private AudioPage GetAudio() => _audio ??= new AudioPage();
    private MemoryPage GetMemory() => _memory ??= new MemoryPage();
    private SettingsPage GetSettings() => _settings ??= new SettingsPage();

    private void ShowPage(string title, object page)
    {
        try
        {
            PageTitle.Text = title;
            PageContent.Content = page;
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo abrir la sección {title}");
            MessageBox.Show(
                $"No se pudo abrir esta sección. EV seguirá funcionando.\n\n{ex.Message}",
                "EV",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void VoiceRecognition_StatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            VoiceStatusText.Text = status;
            VoiceStatusDot.Fill = status.Contains("Escuchando", StringComparison.OrdinalIgnoreCase)
                ? Brushes.LightGreen
                : Brushes.Orange;
        });
    }

    private async void VoiceRecognition_CommandRecognized(object? sender, VoiceCommandEventArgs e)
    {
        try
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                VoiceStatusText.Text = "Te escuché · procesando...";
                VoiceStatusDot.Fill = Brushes.LightGreen;

                if (string.IsNullOrWhiteSpace(e.Command))
                {
                    await _voiceOutput.SpeakAsync("Sí, señor.");
                    return;
                }

                App.LogException(
                    new InvalidOperationException($"Comando de voz recibido: {e.Command}"),
                    "Registro de comando de voz");

                await _voiceOutput.SpeakAsync($"Entendido, señor. Dijiste: {e.Command}");
            });
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al procesar un comando de voz");
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("Inicio", GetHome());
    private void Audio_Click(object sender, RoutedEventArgs e) => ShowPage("Audio", GetAudio());
    private void Memory_Click(object sender, RoutedEventArgs e) => ShowPage("Memoria", GetMemory());
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage("Configuración", GetSettings());

    protected override void OnClosed(EventArgs e)
    {
        _voiceRecognition.Dispose();
        _voiceOutput.Dispose();
        _audio?.Dispose();
        base.OnClosed(e);
    }
}
