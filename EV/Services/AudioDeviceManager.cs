using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EV.Services;

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetInputs() => GetDevices(DataFlow.Capture);
    public IReadOnlyList<AudioDeviceInfo> GetOutputs() => GetDevices(DataFlow.Render);

    public bool TryGetDeviceName(string id, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(id)) return false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            name = device.FriendlyName;
            return !string.IsNullOrWhiteSpace(name);
        }
        catch { return false; }
    }

    public MMDevice? TryGetInput(string? preferredId)
        => TryGetDevice(DataFlow.Capture, preferredId);

    public MMDevice? TryGetOutput(string? preferredId)
        => TryGetDevice(DataFlow.Render, preferredId);

    private static MMDevice? TryGetDevice(DataFlow flow, string? preferredId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                try
                {
                    var preferred = enumerator.GetDevice(preferredId);
                    if (preferred.State == DeviceState.Active)
                        return preferred;
                    preferred.Dispose();
                }
                catch { }
            }

            try
            {
                var fallback = enumerator.GetDefaultAudioEndpoint(
                    flow,
                    Role.Multimedia);

                if (fallback.State == DeviceState.Active)
                    return fallback;

                fallback.Dispose();
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    return device;
                }
                catch
                {
                    device.Dispose();
                }
            }
        }
        catch { }

        return null;
    }

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

            foreach (var device in collection)
            {
                try
                {
                    var id = device.ID;
                    var name = device.FriendlyName;
                    if (!string.IsNullOrWhiteSpace(id))
                        devices.Add(new AudioDeviceInfo(id, string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name));
                }
                catch { }
                finally { device.Dispose(); }
            }
        }
        catch { }

        return devices;
    }
}

public sealed record AudioDeviceInfo(string Id, string Name);
