using NAudio.CoreAudioApi;

namespace EV.Services;

public sealed class AudioDeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<string> GetInputs() =>
        _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(x => x.FriendlyName).ToList();

    public IReadOnlyList<string> GetOutputs() =>
        _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(x => x.FriendlyName).ToList();

    public void Dispose() => _enumerator.Dispose();
}