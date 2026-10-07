using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EV.Services;

namespace EV.Pages;

public partial class DiagnosticsPage : UserControl
{
    private readonly AudioDeviceManager _audioDevices = new();
    private readonly AudioSettingsStore _audioSettings = new();
    private readonly AppSettingsStore _appSettings = new();
    private readonly WindowsStartupService _startup = new();
    private readonly AiSettingsStore _aiSettings = new();
    private readonly MemorySyncService _memory = new();

    public DiagnosticsPage()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        try
        {
            var appSettings = _appSettings.Load();
            var inputs = _audioDevices.GetInputs().ToArray();
            var outputs = _audioDevices.GetOutputs().ToArray();
            var audio = _audioSettings.Load();
            var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-es-0.42");
            var ai = _aiSettings.Load();
            var account = _memory.GetAccount();
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EV",
                "ev.log");

            SetStatus(AppStatus, $"EV · .NET {Environment.Version} · {Environment.OSVersion.VersionString}");
            SetStatus(InputStatus, inputs.Length == 0
                ? "ERROR · No se detectaron micrófonos activos."
                : $"OK · {inputs.Length} detectado(s). Preferido: {audio.PreferredInputName ?? "automático"}");
            SetStatus(OutputStatus, outputs.Length == 0
                ? "ERROR · No se detectaron salidas de audio activas."
                : $"OK · {outputs.Length} detectada(s). Preferida: {audio.PreferredOutputName ?? "automática"}");
            SetStatus(VoskStatus, Directory.Exists(modelPath)
                ? $"OK · Modelo encontrado en {modelPath}"
                : $"ERROR · Falta el modelo en {modelPath}");
            SetStatus(AiStatus, string.IsNullOrWhiteSpace(ai.ApiKey)
                ? "AVISO · No hay una clave de IA configurada."
                : $"OK · Modelo: {ai.Model}");
            SetStatus(MemoryStatus, account is null
                ? "AVISO · EV no está vinculado a una cuenta de memoria."
                : "OK · Memoria vinculada y disponible para sincronización.");
            SetStatus(StartupStatus, appSettings.StartWithWindows
                ? (_startup.IsEnabled() ? "OK · EV está configurado para iniciar con Windows." : "AVISO · La opción está activa, pero Windows no tiene la entrada.")
                : "Desactivado.");
            SetStatus(LogStatus, File.Exists(logPath)
                ? $"OK · {logPath}"
                : "Sin errores registrados todavía.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo actualizar el diagnóstico");
            SetStatus(AppStatus, "ERROR · No se pudo completar el diagnóstico.");
        }
    }

    private static void SetStatus(TextBlock target, string text)
    {
        target.Text = text;
        target.Foreground = text.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
            ? Brushes.LightGreen
            : text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                ? Brushes.OrangeRed
                : Brushes.Gold;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EV");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $""{folder}"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo abrir la carpeta de EV");
        }
    }
}