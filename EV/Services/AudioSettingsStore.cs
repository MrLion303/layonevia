using System.IO;
using System.Text.Json;

namespace EV.Services;

public sealed class AudioSettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EV",
        "audio.json");

    public AudioSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AudioSettings();

            return JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(_path))
                   ?? new AudioSettings();
        }
        catch
        {
            return new AudioSettings();
        }
    }

    public void Save(AudioSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }
}

public sealed class AudioSettings
{
    public string? PreferredInputId { get; set; }
    public string? PreferredInputName { get; set; }
    public string? PreferredOutputId { get; set; }
    public string? PreferredOutputName { get; set; }
    public string? PreferredVoiceName { get; set; }
    public int VoiceRate { get; set; } = 0;
    public int VoiceVolume { get; set; } = 100;
}
