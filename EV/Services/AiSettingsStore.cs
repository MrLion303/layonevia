using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EV.Services;

public sealed class AiSettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EV",
        "ai.json");

    public AiSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AiSettings();

            var data = JsonSerializer.Deserialize<StoredAiSettings>(File.ReadAllText(_path));
            if (data is null)
                return new AiSettings();

            return new AiSettings
            {
                ApiKey = Unprotect(data.ApiKey),
                Model = string.IsNullOrWhiteSpace(data.Model) ? "gpt-6-luna" : data.Model,
                Endpoint = string.IsNullOrWhiteSpace(data.Endpoint)
                    ? "https://api.openai.com/v1/responses"
                    : data.Endpoint
            };
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron cargar los ajustes de IA");
            return new AiSettings();
        }
    }

    public void Save(AiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var stored = new StoredAiSettings
            {
                ApiKey = Protect(settings.ApiKey),
                Model = settings.Model,
                Endpoint = settings.Endpoint
            };

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron guardar los ajustes de IA");
            throw;
        }
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            null,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(value),
                null,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class AiSettings
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-6-luna";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";
}

file sealed class StoredAiSettings
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-6-luna";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";
}
