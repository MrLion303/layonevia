using System.IO;
using System.Text.Json;

namespace EV.Services;

public sealed class RoutineStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EV",
        "routines.json");

    public IReadOnlyList<EvRoutine> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];

            return JsonSerializer.Deserialize<List<EvRoutine>>(File.ReadAllText(_path)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<EvRoutine> routines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(routines, new JsonSerializerOptions { WriteIndented = true }));
    }

    public EvRoutine? Find(string name)
        => Load().FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class EvRoutine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<RoutineStep> Steps { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RoutineStep
{
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
}
