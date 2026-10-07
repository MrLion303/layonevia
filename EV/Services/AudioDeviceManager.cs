using NAudio.CoreAudioApi;

namespace EV.Services;

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetInputs() => GetDevices(DataFlow.Capture);
    public IReadOnlyList<AudioDeviceInfo> GetOutputs() => GetDevices(DataFlow.Render);

    public bool TryGetDeviceName(string id, out string name)
    {
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            return false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            name = device.FriendlyName;
            return !string.IsNullOrWhiteSpace(name);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

            foreach (var device in collection)
            {
                try
                {
                    var id = device.ID;
                    var name = device.FriendlyName;

                    if (!string.IsNullOrWhiteSpace(id))
                        devices.Add(new AudioDeviceInfo(
                            id,
                            string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name));
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
        catch
        {
        }

        return devices;
    }
}

public sealed record AudioDeviceInfo(string Id, string Name);
