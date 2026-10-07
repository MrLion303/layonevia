using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EV.Services;

public sealed class GitHubMemorySync
{
    private readonly HttpClient _http;

    public GitHubMemorySync()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EV/0.1");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<bool> CanReachGitHubAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                "https://api.github.com",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitHubFile?> DownloadMemoryAsync(
        string repository,
        string token,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(token))
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildContentsUrl(repository, path));

        AddAuthorization(request, token);

        using var response = await _http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub rechazó la lectura de la memoria ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(
            cancellationToken: cancellationToken);

        if (payload?.Content is null || string.IsNullOrWhiteSpace(payload.Sha))
            throw new InvalidDataException("GitHub devolvió una memoria incompleta.");

        try
        {
            var encoded = payload.Content.Replace("\n", string.Empty);
            var content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return new GitHubFile(payload.Sha, content);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("La memoria de GitHub no contiene Base64 válido.", ex);
        }
    }

    public async Task<bool> SaveMemoryAsync(
        string repository,
        string token,
        string path,
        string content,
        string? existingSha = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(token))
            return false;

        var sha = existingSha;

        if (string.IsNullOrWhiteSpace(sha))
        {
            var current = await DownloadMemoryAsync(repository, token, path, cancellationToken);
            sha = current?.Sha;
        }

        var success = await PutMemoryAsync(
            repository, token, path, content, sha, cancellationToken);

        if (success)
            return true;

        // Otro PC pudo actualizar la memoria entre la descarga y el guardado.
        // Volvemos a leer el SHA y lo intentamos una vez para evitar sobrescribirlo.
        if (!string.IsNullOrWhiteSpace(sha))
        {
            var current = await DownloadMemoryAsync(repository, token, path, cancellationToken);
            if (current is null)
                return false;

            return await PutMemoryAsync(
                repository, token, path, content, current.Sha, cancellationToken);
        }

        return false;
    }

    private async Task<bool> PutMemoryAsync(
        string repository,
        string token,
        string path,
        string content,
        string? sha,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            BuildContentsUrl(repository, path));

        AddAuthorization(request, token);

        var body = new Dictionary<string, object?>
        {
            ["message"] = "Actualizar memoria de EV",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
        };

        if (!string.IsNullOrWhiteSpace(sha))
            body["sha"] = sha;

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static string BuildContentsUrl(string repository, string path)
    {
        var safeRepository = repository.Trim().Trim('/');
        var safePath = string.Join(
            "/",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        return $"https://api.github.com/repos/{safeRepository}/contents/{safePath}";
    }

    private static void AddAuthorization(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

public sealed record GitHubFile(string Sha, string Content);

internal sealed class GitHubContentResponse
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}
