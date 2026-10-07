using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EV.Pages;
using EV.Services;

namespace EV;

public partial class MainWindow : Window
{
    private readonly VoiceRecognitionService _voiceRecognition = new();
    private readonly VoiceOutputService _voiceOutput = new();
    private readonly CommandEngine _commandEngine = new();
    private readonly MemorySyncService _memorySync = new();
    private readonly DispatcherTimer _memorySyncTimer;
    private readonly AppSettingsStore _appSettings = new();
    private readonly WindowsStartupService _startup = new();

    private HomePage? _home;
    private AudioPage? _audio;
    private MemoryPage? _memory;
    private ToolsPage? _tools;
    private RoutinesPage? _routines;
    private DiagnosticsPage? _diagnostics;
    private SettingsPage? _settings;
    private bool _allowClose;
    private bool _isHiddenToTray;

    public MainWindow()
    {
        InitializeComponent();

        var settings = _appSettings.Load();
        _voiceRecognition.WakeWordEnabled = settings.WakeWordEnabled;
        if (!settings.StartWithWindows && _startup.IsEnabled())
        {
            try { _startup.SetEnabled(false); } catch { }
        }

        _voiceRecognition.StatusChanged += VoiceRecognition_StatusChanged;
        _voiceRecognition.AudioLevelChanged += VoiceRecognition_AudioLevelChanged;
        _voiceRecognition.CommandRecognized += VoiceRecognition_CommandRecognized;
        _voiceOutput.AudioLevelChanged += VoiceOutput_AudioLevelChanged;

        _memorySyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _memorySyncTimer.Tick += MemorySyncTimer_Tick;
        _memorySyncTimer.Start();

        ShowPage("Inicio", GetHome());

        if (settings.WakeWordEnabled)
            _voiceRecognition.Start();
        else
            VoiceStatusText.Text = "Escucha de activación desactivada";

        _ = SyncMemoryOnStartupAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Any(x => string.Equals(x, "--background", StringComparison.OrdinalIgnoreCase)))
            HideToTray();
    }

    private async Task SyncMemoryOnStartupAsync()
    {
        try
        {
            if (_memorySync.GetAccount() is null)
                return;

            await _memorySync.SyncAsync();
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo sincronizar la memoria al iniciar EV");
        }
    }

    private async void MemorySyncTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_memorySync.GetAccount() is not null)
                await _memorySync.SyncAsync();
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo sincronizar la memoria automáticamente");
        }
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

    private void Audio_InputDeviceChanged(object? sender, EventArgs e) => _voiceRecognition.Restart();

    private MemoryPage GetMemory() => _memory ??= new MemoryPage();
    private ToolsPage GetTools() => _tools ??= new ToolsPage();
    private RoutinesPage GetRoutines() => _routines ??= new RoutinesPage();
    private DiagnosticsPage GetDiagnostics() => _diagnostics ??= new DiagnosticsPage();

    private SettingsPage GetSettings()
    {
        if (_settings is not null)
            return _settings;

        _settings = new SettingsPage();
        _settings.SettingsChanged += Settings_SettingsChanged;
        return _settings;
    }

    private void Settings_SettingsChanged(object? sender, EventArgs e)
    {
        var settings = _appSettings.Load();
        _voiceRecognition.WakeWordEnabled = settings.WakeWordEnabled;

        if (settings.WakeWordEnabled)
        {
            if (!_voiceRecognition.IsListening)
                _voiceRecognition.Start();
        }
        else
        {
            _voiceRecognition.Stop();
            VoiceStatusText.Text = "Escucha de activación desactivada";
            VoiceStatusDot.Fill = Brushes.Orange;
        }
    }

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

    private void VoiceRecognition_AudioLevelChanged(object? sender, double level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => VoiceRecognition_AudioLevelChanged(sender, level));
            return;
        }

        VoiceWave.SetLevel(level);
        VoiceAudioText.Text = level > 0.03
            ? $"Micrófono: recibiendo audio · {level:P0}"
            : "Micrófono: escuchando...";
    }

    private void VoiceOutput_AudioLevelChanged(object? sender, double level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => VoiceOutput_AudioLevelChanged(sender, level));
            return;
        }

        GetHome().SetSpeechLevel(level);
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

            _voiceRecognition.Stop();
            VoiceStatusText.Text = "Hablando...";
            if (_appSettings.Load().VoiceResponseEnabled)
                await _voiceOutput.SpeakAsync(result.Response);

            if (!IsLoaded)
                return;

            _voiceRecognition.Start();

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

    private void HideToTray()
    {
        if (_allowClose)
            return;

        _isHiddenToTray = true;
        Hide();
    }

    private void ShowFromTray()
    {
        _isHiddenToTray = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;

        var settings = _appSettings.Load();
        if (settings.MinimizeToTray)
            HideToTray();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var settings = _appSettings.Load();
        if (_allowClose || !settings.MinimizeToTray)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayWake_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _appSettings.Load();
            if (!settings.WakeWordEnabled)
            {
                settings.WakeWordEnabled = true;
                _appSettings.Save(settings);
                _voiceRecognition.WakeWordEnabled = true;
            }

            if (!_voiceRecognition.IsListening)
                _voiceRecognition.Start();

            VoiceStatusText.Text = "Escuchando «Oye ibi»";
            VoiceStatusDot.Fill = Brushes.LightGreen;
            ShowFromTray();
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo reactivar la escucha desde la bandeja");
        }
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        try { TrayIcon.Dispose(); } catch { }
        Close();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("Inicio", GetHome());
    private void Audio_Click(object sender, RoutedEventArgs e) => ShowPage("Audio", GetAudio());
    private void Memory_Click(object sender, RoutedEventArgs e) => ShowPage("Memoria", GetMemory());
    private void Tools_Click(object sender, RoutedEventArgs e) => ShowPage("Aplicaciones y archivos", GetTools());
    private void Routines_Click(object sender, RoutedEventArgs e) => ShowPage("Rutinas", GetRoutines());
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => ShowPage("Diagnóstico", GetDiagnostics());
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage("Configuración", GetSettings());

    protected override void OnClosed(EventArgs e)
    {
        _allowClose = true;
        _voiceRecognition.StatusChanged -= VoiceRecognition_StatusChanged;
        _voiceRecognition.AudioLevelChanged -= VoiceRecognition_AudioLevelChanged;
        _voiceOutput.AudioLevelChanged -= VoiceOutput_AudioLevelChanged;
        _memorySyncTimer.Stop();
        _memorySyncTimer.Tick -= MemorySyncTimer_Tick;
        _voiceRecognition.Dispose();
        _voiceOutput.Dispose();

        if (_audio is not null)
        {
            _audio.InputDeviceChanged -= Audio_InputDeviceChanged;
            _audio.Dispose();
        }

        if (_settings is not null)
            _settings.SettingsChanged -= Settings_SettingsChanged;

        try { TrayIcon.Dispose(); } catch { }
        base.OnClosed(e);
    }
}