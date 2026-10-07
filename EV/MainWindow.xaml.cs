using System.Windows;
using System.Windows.Media;
using EV.Pages;
using EV.Services;

namespace EV;

public partial class MainWindow : Window
{
    private readonly VoiceRecognitionService _voiceRecognition = new();
    private readonly VoiceOutputService _voiceOutput = new();
    private readonly CommandEngine _commandEngine = new();

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
    private AudioPage GetAudio()
    {
        if (_audio is not null)
            return _audio;

        _audio = new AudioPage();
        _audio.InputDeviceChanged += Audio_InputDeviceChanged;
        return _audio;
    }

    private void Audio_InputDeviceChanged(object? sender, EventArgs e)
    {
        _voiceRecognition.Restart();
    }
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => VoiceRecognition_StatusChanged(sender, status));
            return;
        }

        VoiceStatusText.Text = status;
        VoiceStatusDot.Fill = status.Contains("Escuchando", StringComparison.OrdinalIgnoreCase)
            ? Brushes.LightGreen
            : Brushes.Orange;
    }

    private async void VoiceRecognition_CommandRecognized(object? sender, VoiceCommandEventArgs e)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => VoiceRecognition_CommandRecognized(sender, e));
                return;
            }

            VoiceStatusText.Text = "Te escuché · procesando...";
            VoiceStatusDot.Fill = Brushes.LightGreen;

            var result = await _commandEngine.ExecuteAsync(e.Command);

            await _voiceOutput.SpeakAsync(result.Response);

            VoiceStatusText.Text = _voiceRecognition.IsListening
                ? "Escuchando «Oye ibi»"
                : "Reconocimiento de voz detenido.";
            VoiceStatusDot.Fill = _voiceRecognition.IsListening
                ? Brushes.LightGreen
                : Brushes.Orange;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al procesar un comando de voz");
            VoiceStatusText.Text = "Error al procesar la orden.";
            VoiceStatusDot.Fill = Brushes.Orange;
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
        if (_audio is not null)
        {
            _audio.InputDeviceChanged -= Audio_InputDeviceChanged;
            _audio.Dispose();
        }

        base.OnClosed(e);
    }
}
