using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using EV.Services;

namespace EV.Pages;

public partial class AudioPage : UserControl
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SpeechSynthesizer _speaker = new();
    private readonly List<AudioDeviceOption> _inputs = [];
    private readonly List<AudioDeviceOption> _outputs = [];
    private bool _loading;

    public AudioPage()
    {
        InitializeComponent();
        InputDevice.SelectionChanged += InputDevice_SelectionChanged;
        OutputDevice.SelectionChanged += OutputDevice_SelectionChanged;
        Loaded += (_, _) => LoadDevices();
        Unloaded += (_, _) => _speaker.SpeakAsyncCancelAll();
    }

    private void LoadDevices()
    {
        _loading = true;

        try
        {
            InputDevice.Items.Clear();
            OutputDevice.Items.Clear();
            _inputs.Clear();
            _outputs.Clear();

            InputDevice.Items.Add(new AudioDeviceOption("Automático", null));
            OutputDevice.Items.Add(new AudioDeviceOption("Automático", null));

            try
            {
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    try
                    {
                        _inputs.Add(new AudioDeviceOption(device.FriendlyName, device.ID));
                    }
                    catch
                    {
                        // Un dispositivo que desaparezca durante la enumeración no debe cerrar EV.
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                InputStatus.Text = $"No se pudieron cargar los micrófonos: {ex.Message}";
            }

            try
            {
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        _outputs.Add(new AudioDeviceOption(device.FriendlyName, device.ID));
                    }
                    catch
                    {
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                OutputStatus.Text = $"No se pudieron cargar las salidas: {ex.Message}";
            }

            foreach (var item in _inputs)
                InputDevice.Items.Add(item);

            foreach (var item in _outputs)
                OutputDevice.Items.Add(item);

            InputDevice.SelectedIndex = 0;
            OutputDevice.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            InputStatus.Text = $"Error al cargar dispositivos de audio: {ex.Message}";
            OutputStatus.Text = "EV seguirá funcionando sin cambiar el dispositivo.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void InputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || InputDevice.SelectedItem is not AudioDeviceOption selected)
            return;

        InputStatus.Text = selected.Id is null
            ? "EV elegirá automáticamente un micrófono disponible."
            : $"Micrófono seleccionado: {selected.Name}";
    }

    private void OutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputDevice.SelectedItem is not AudioDeviceOption selected)
            return;

        OutputStatus.Text = selected.Id is null
            ? "EV elegirá automáticamente una salida disponible."
            : $"Salida seleccionada: {selected.Name}";
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
                InputStatus.Text = "EV usará automáticamente un micrófono disponible.";
                return;
            }

            using var device = _enumerator.GetDevice(selected.Id);
            InputStatus.Text = $"Micrófono disponible: {device.FriendlyName}";
        }
        catch
        {
            InputStatus.Text = "Ese micrófono ya no está disponible. EV usará otro dispositivo si puede.";
        }
    }

    private void TestOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _speaker.SpeakAsyncCancelAll();
            _speaker.SpeakAsync("Hola, señor. Esta es una prueba de audio de EV.");
            OutputStatus.Text = "Reproduciendo prueba de voz.";
        }
        catch (Exception ex)
        {
            OutputStatus.Text = $"No se pudo reproducir la prueba: {ex.Message}";
        }
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);

        if (VisualParent is null)
            _speaker.SpeakAsyncCancelAll();
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