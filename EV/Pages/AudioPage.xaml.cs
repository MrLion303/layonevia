using System.Windows;
using System.Windows.Controls;
using EV.Services;

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

    public event EventHandler? InputDeviceChanged;

    public AudioPage()
    {
        InitializeComponent();
        InputDevice.SelectionChanged += InputDevice_SelectionChanged;
        OutputDevice.SelectionChanged += OutputDevice_SelectionChanged;
        VoiceSelection.SelectionChanged += VoiceSelection_SelectionChanged;
        VoiceRate.ValueChanged += VoiceRate_ValueChanged;
        VoiceVolume.ValueChanged += VoiceVolume_ValueChanged;
        Loaded += AudioPage_Loaded;
    }

    private void AudioPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed) LoadDevices();
    }

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
            LoadVoiceSettings(settings);
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar los dispositivos de audio");
            InputStatus.Text = "No se pudieron cargar los micrófonos. EV seguirá funcionando.";
            OutputStatus.Text = "No se pudieron cargar las salidas. EV seguirá funcionando.";
        }
        finally { _loading = false; }
    }

    private void LoadVoiceSettings(AudioSettings settings)
    {
        _loading = true;
        try
        {
            VoiceSelection.Items.Clear();
            foreach (var voice in VoiceOutputService.GetAvailableVoices())
                VoiceSelection.Items.Add(voice);

            if (!string.IsNullOrWhiteSpace(settings.PreferredVoiceName))
            {
                var index = VoiceSelection.Items.IndexOf(settings.PreferredVoiceName);
                if (index >= 0)
                    VoiceSelection.SelectedIndex = index;
            }

            if (VoiceSelection.SelectedIndex < 0 && VoiceSelection.Items.Count > 0)
                VoiceSelection.SelectedIndex = 0;

            VoiceRate.Value = Math.Clamp(settings.VoiceRate, -10, 10);
            VoiceVolume.Value = Math.Clamp(settings.VoiceVolume, 0, 100);
            VoiceStatus.Text = VoiceSelection.Items.Count == 0
                ? "Windows no tiene voces SAPI5 disponibles."
                : $"Voz seleccionada: {VoiceSelection.SelectedItem}";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar las voces de EV");
            VoiceStatus.Text = "No se pudieron cargar las voces instaladas.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void VoiceSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _disposed) return;
        var settings = _settingsStore.Load();
        settings.PreferredVoiceName = VoiceSelection.SelectedItem as string;
        _settingsStore.Save(settings);
        VoiceStatus.Text = $"Voz guardada: {settings.PreferredVoiceName ?? "automática"}";
    }

    private void VoiceRate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _disposed) return;
        var settings = _settingsStore.Load();
        settings.VoiceRate = (int)Math.Round(VoiceRate.Value);
        _settingsStore.Save(settings);
    }

    private void VoiceVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _disposed) return;
        var settings = _settingsStore.Load();
        settings.VoiceVolume = (int)Math.Round(VoiceVolume.Value);
        _settingsStore.Save(settings);
    }

    private async void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VoiceStatus.Text = "Probando voz...";
            var deviceName = await _voice.SpeakAsync("Hola, señor. Esta es la voz que EV tiene seleccionada.");
            VoiceStatus.Text = deviceName is null
                ? "No hay una salida de audio disponible."
                : $"Voz reproducida por: {deviceName}";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al probar la voz de EV");
            VoiceStatus.Text = "No se pudo reproducir la voz.";
        }
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
        InputDeviceChanged?.Invoke(this, EventArgs.Empty);
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
                "Hola, soy ibi. Esta es una prueba de mi salida de audio.");

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
        VoiceSelection.SelectionChanged -= VoiceSelection_SelectionChanged;
        VoiceRate.ValueChanged -= VoiceRate_ValueChanged;
        VoiceVolume.ValueChanged -= VoiceVolume_ValueChanged;
        Loaded -= AudioPage_Loaded;
    }
}

public sealed class AudioDeviceOption
{
    public string Name { get; }
    public string? Id { get; }
    public AudioDeviceOption(string name, string? id) { Name = string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name; Id = id; }
    public override string ToString() => Name;
}
