using System.Windows;
using System.Windows.Controls;
using EV.Services;
using NAudio.CoreAudioApi;

namespace EV.Pages;

public partial class AudioPage : UserControl, IDisposable
{
    private readonly AudioDeviceManager _devices = new();
    private readonly AudioSettingsStore _settingsStore = new();
    private readonly VoiceOutputService _voice = new();
    private readonly List<AudioDeviceOption> _inputs = [];
    private readonly List<AudioDeviceOption> _outputs = [];
    private bool _loading;
    private bool _disposed;

    public AudioPage()
    {
        InitializeComponent();
        InputDevice.SelectionChanged += InputDevice_SelectionChanged;
        OutputDevice.SelectionChanged += OutputDevice_SelectionChanged;
        Loaded += AudioPage_Loaded;
        Unloaded += AudioPage_Unloaded;
    }

    private void AudioPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed) LoadDevices();
    }

    private void AudioPage_Unloaded(object sender, RoutedEventArgs e) => StopSpeech();

    private void LoadDevices()
    {
        _loading = true;
        try
        {
            var settings = _settingsStore.Load();
            _inputs.Clear(); _outputs.Clear();

            foreach (var d in _devices.GetInputs()) _inputs.Add(new AudioDeviceOption(d.Name, d.Id));
            foreach (var d in _devices.GetOutputs()) _outputs.Add(new AudioDeviceOption(d.Name, d.Id));

            InputDevice.Items.Clear(); OutputDevice.Items.Clear();
            InputDevice.Items.Add(new AudioDeviceOption("Automático", null));
            OutputDevice.Items.Add(new AudioDeviceOption("Automático", null));
            foreach (var d in _inputs) InputDevice.Items.Add(d);
            foreach (var d in _outputs) OutputDevice.Items.Add(d);

            SelectPreferredInput(settings);
            SelectPreferredOutput(settings);
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar los dispositivos de audio");
            InputStatus.Text = "No se pudieron cargar los micrófonos. EV seguirá funcionando.";
            OutputStatus.Text = "No se pudieron cargar las salidas. EV seguirá funcionando.";
        }
        finally { _loading = false; }
    }

    private void SelectPreferredInput(AudioSettings s)
    {
        var i = FindDeviceIndex(InputDevice, s.PreferredInputId);
        InputDevice.SelectedIndex = i >= 0 ? i : 0;
        InputStatus.Text = i >= 0
            ? $"Micrófono preferido: {_inputs[i - 1].Name}"
            : string.IsNullOrWhiteSpace(s.PreferredInputId)
                ? "EV elegirá automáticamente un micrófono disponible."
                : $"No está disponible «{s.PreferredInputName ?? "el micrófono preferido"}». EV usará otro temporalmente.";
    }

    private void SelectPreferredOutput(AudioSettings s)
    {
        var i = FindDeviceIndex(OutputDevice, s.PreferredOutputId);
        OutputDevice.SelectedIndex = i >= 0 ? i : 0;
        OutputStatus.Text = i >= 0
            ? $"Salida preferida: {_outputs[i - 1].Name}"
            : string.IsNullOrWhiteSpace(s.PreferredOutputId)
                ? "EV elegirá automáticamente una salida disponible."
                : $"No está disponible «{s.PreferredOutputName ?? "la salida preferida"}». EV usará otra temporalmente.";
    }

    private static int FindDeviceIndex(ComboBox box, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        for (var i = 1; i < box.Items.Count; i++)
            if (box.Items[i] is AudioDeviceOption x && string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private void InputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _disposed || InputDevice.SelectedItem is not AudioDeviceOption selected) return;
        var s = _settingsStore.Load();
        s.PreferredInputId = selected.Id; s.PreferredInputName = selected.Id is null ? null : selected.Name;
        _settingsStore.Save(s);
        InputStatus.Text = selected.Id is null ? "EV elegirá automáticamente un micrófono disponible." : $"Micrófono preferido guardado: {selected.Name}";
    }

    private void OutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _disposed || OutputDevice.SelectedItem is not AudioDeviceOption selected) return;
        var s = _settingsStore.Load();
        s.PreferredOutputId = selected.Id; s.PreferredOutputName = selected.Id is null ? null : selected.Name;
        _settingsStore.Save(s);
        OutputStatus.Text = selected.Id is null ? "EV elegirá automáticamente una salida disponible." : $"Salida preferida guardada: {selected.Name}";
    }

    private void TestInput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = _settingsStore.Load();
            using var device = _devices.TryGetInput(s.PreferredInputId);
            InputStatus.Text = device is null ? "No hay un micrófono disponible." : $"Micrófono activo: {device.FriendlyName}";
        }
        catch (Exception ex) { App.LogException(ex, "Error al probar el micrófono"); InputStatus.Text = "No se pudo probar el micrófono."; }
    }

    private async void TestOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OutputStatus.Text = "Reproduciendo prueba de voz...";
            var deviceName = await _voice.SpeakAsync(
                "Hola, soy EV. Esta es una prueba de mi salida de audio.");

            OutputStatus.Text = deviceName is null
                ? "No hay una salida de audio disponible."
                : $"Prueba reproducida por: {deviceName}";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al probar la salida de voz");
            OutputStatus.Text = "No se pudo reproducir la prueba de voz.";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _voice.Dispose();
        InputDevice.SelectionChanged -= InputDevice_SelectionChanged;
        OutputDevice.SelectionChanged -= OutputDevice_SelectionChanged;
        Loaded -= AudioPage_Loaded; Unloaded -= AudioPage_Unloaded;
    }
}

public sealed class AudioDeviceOption
{
    public string Name { get; }
    public string? Id { get; }
    public AudioDeviceOption(string name, string? id) { Name = string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name; Id = id; }
    public override string ToString() => Name;
}
