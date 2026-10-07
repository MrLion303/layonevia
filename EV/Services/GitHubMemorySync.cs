using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EV.Services;

public sealed class GitHubMemorySync
{
    private readonly HttpClient _http = new();

    public async Task<bool> CanReachGitHubAsync()
    {
        try
        {
            using var response = await _http.GetAsync("https://api.github.com", HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> UploadMemoryAsync(string repository, string token, string path, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"https://api.github.com/repos/{repository}/contents/{path}");
            request.Headers.UserAgent.ParseAdd("EV/0.1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                message = "Actualizar memoria de EV",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode || (int)response.StatusCode == 422;
        }
        catch { return false; }
    }
}