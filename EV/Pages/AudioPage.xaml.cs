using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class AudioPage : UserControl, IDisposable
{
    private readonly AudioDeviceManager _devices = new();
    private readonly AudioSettingsStore _settingsStore = new();
    private readonly SpeechSynthesizer _speaker = new();
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
        if (!_disposed)
            LoadDevices();
    }

    private void AudioPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopSpeech();
    }

    private void LoadDevices()
    {
        if (_disposed)
            return;

        _loading = true;

        try
        {
            var settings = _settingsStore.Load();
            var inputs = _devices.GetInputs();
            var outputs = _devices.GetOutputs();

            _inputs.Clear();
            _outputs.Clear();

            foreach (var device in inputs)
                _inputs.Add(new AudioDeviceOption(device.Name, device.Id));

            foreach (var device in outputs)
                _outputs.Add(new AudioDeviceOption(device.Name, device.Id));

            InputDevice.Items.Clear();
            OutputDevice.Items.Clear();

            InputDevice.Items.Add(new AudioDeviceOption("Automático", null));
            OutputDevice.Items.Add(new AudioDeviceOption("Automático", null));

            foreach (var item in _inputs)
                InputDevice.Items.Add(item);

            foreach (var item in _outputs)
                OutputDevice.Items.Add(item);

            SelectPreferredInput(settings);
            SelectPreferredOutput(settings);

            if (_inputs.Count == 0)
                InputStatus.Text = "No se encontró ningún micrófono activo. EV usará el dispositivo disponible cuando aparezca.";
            if (_outputs.Count == 0)
                OutputStatus.Text = "No se encontró ninguna salida activa. EV usará la salida de Windows cuando esté disponible.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar los dispositivos de audio");
            InputStatus.Text = "No se pudieron cargar los micrófonos. EV seguirá funcionando.";
            OutputStatus.Text = "No se pudieron cargar las salidas. EV seguirá funcionando.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectPreferredInput(AudioSettings settings)
    {
        var index = FindDeviceIndex(InputDevice, settings.PreferredInputId);

        if (index >= 0)
        {
            InputDevice.SelectedIndex = index;
            InputStatus.Text = $"Micrófono preferido: {_inputs[index - 1].Name}";
        }
        else
        {
            InputDevice.SelectedIndex = 0;
            InputStatus.Text = string.IsNullOrWhiteSpace(settings.PreferredInputId)
                ? "EV elegirá automáticamente un micrófono disponible."
                : $"No está disponible «{settings.PreferredInputName ?? "el micrófono preferido"}». EV usará otro temporalmente.";
        }
    }

    private void SelectPreferredOutput(AudioSettings settings)
    {
        var index = FindDeviceIndex(OutputDevice, settings.PreferredOutputId);

        if (index >= 0)
        {
            OutputDevice.SelectedIndex = index;
            OutputStatus.Text = $"Salida preferida: {_outputs[index - 1].Name}";
        }
        else
        {
            OutputDevice.SelectedIndex = 0;
            OutputStatus.Text = string.IsNullOrWhiteSpace(settings.PreferredOutputId)
                ? "EV elegirá automáticamente una salida disponible."
                : $"No está disponible «{settings.PreferredOutputName ?? "la salida preferida"}». EV usará otra temporalmente.";
        }
    }

    private static int FindDeviceIndex(ComboBox comboBox, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return -1;

        for (var i = 1; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is AudioDeviceOption item &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private void InputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _disposed || InputDevice.SelectedItem is not AudioDeviceOption selected)
            return;

        var settings = _settingsStore.Load();
        settings.PreferredInputId = selected.Id;
        settings.PreferredInputName = selected.Id is null ? null : selected.Name;
        _settingsStore.Save(settings);

        InputStatus.Text = selected.Id is null
            ? "EV elegirá automáticamente un micrófono disponible."
            : $"Micrófono preferido guardado: {selected.Name}";
    }

    private void OutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _disposed || OutputDevice.SelectedItem is not AudioDeviceOption selected)
            return;

        var settings = _settingsStore.Load();
        settings.PreferredOutputId = selected.Id;
        settings.PreferredOutputName = selected.Id is null ? null : selected.Name;
        _settingsStore.Save(settings);

        OutputStatus.Text = selected.Id is null
            ? "EV elegirá automáticamente una salida disponible."
            : $"Salida preferida guardada: {selected.Name}";
    }

    private void TestInput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (InputDevice.SelectedItem is not AudioDeviceOption selected)
            {
                InputStatus.Text = "Selecciona un micrófono.";
                return;
            }

            if (selected.Id is null)
            {
                InputStatus.Text = "EV buscará automáticamente un micrófono disponible.";
                return;
            }

            if (_devices.TryGetDeviceName(selected.Id, out var name))
                InputStatus.Text = $"Micrófono disponible: {name}";
            else
                InputStatus.Text = "Ese micrófono ya no está disponible. EV usará otro si puede.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al probar el micrófono");
            InputStatus.Text = "No se pudo probar el micrófono.";
        }
    }

    private void TestOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _speaker.SpeakAsyncCancelAll();
            _speaker.SpeakAsync("Hola, señor. Esta es una prueba de audio de EV.");

            var selected = OutputDevice.SelectedItem as AudioDeviceOption;
            OutputStatus.Text = selected?.Id is null
                ? "Reproduciendo por la salida automática de Windows."
                : $"Reproduciendo la prueba. La salida seleccionada es {selected.Name}.";
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al probar la salida de audio");
            OutputStatus.Text = "No se pudo reproducir la prueba de voz.";
        }
    }

    private void StopSpeech()
    {
        try
        {
            _speaker.SpeakAsyncCancelAll();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopSpeech();

        try
        {
            _speaker.Dispose();
        }
        catch
        {
        }

        InputDevice.SelectionChanged -= InputDevice_SelectionChanged;
        OutputDevice.SelectionChanged -= OutputDevice_SelectionChanged;
        Loaded -= AudioPage_Loaded;
        Unloaded -= AudioPage_Unloaded;
    }
}

public sealed class AudioDeviceOption
{
    public string Name { get; }
    public string? Id { get; }

    public AudioDeviceOption(string name, string? id)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name;
        Id = id;
    }

    public override string ToString() => Name;
}
