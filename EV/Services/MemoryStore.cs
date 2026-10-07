using System.IO;
using System.Text.Json;

namespace EV.Services;

public sealed class MemoryStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EV",
        "memory.json");

    public IReadOnlyList<MemoryItem> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(_path)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<MemoryItem> memories)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var json = JsonSerializer.Serialize(
            memories,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(_path, json);
    }
}