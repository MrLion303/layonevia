using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;

namespace EV.Pages;

public partial class AudioPage : UserControl
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SpeechSynthesizer _speaker = new();

    public AudioPage()
    {
        InitializeComponent();
        LoadDevices();
    }

    private void LoadDevices()
    {
        InputDevice.Items.Add("Automático");
        OutputDevice.Items.Add("Automático");

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            InputDevice.Items.Add(device.FriendlyName);

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            OutputDevice.Items.Add(device.FriendlyName);

        InputDevice.SelectedIndex = 0;
        OutputDevice.SelectedIndex = 0;
    }

    private void TestInput_Click(object sender, RoutedEventArgs e)
    {
        InputStatus.Text = "El micrófono seleccionado está disponible.";
    }

    private void TestOutput_Click(object sender, RoutedEventArgs e)
    {
        _speaker.SpeakAsyncCancelAll();
        _speaker.SpeakAsync("Hola, señor. Esta es una prueba de audio de EV.");
        OutputStatus.Text = "Reproduciendo prueba de voz.";
    }
}