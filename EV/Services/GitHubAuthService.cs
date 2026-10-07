using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace EV.Services;

public sealed class GitHubAuthService
{
    private readonly HttpClient _http;

    public GitHubAuthService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EV/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<GitHubDeviceLogin?> StartDeviceLoginAsync(
        CancellationToken cancellationToken = default)
    {
        if (GitHubOAuthSettings.ClientId.StartsWith("PON_AQUI", StringComparison.Ordinal))
            return null;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubOAuthSettings.ClientId,
            ["scope"] = GitHubOAuthSettings.Scope
        });

        using var response = await _http.PostAsync(
            GitHubOAuthSettings.DeviceCodeUrl,
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        var result = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(
            cancellationToken: cancellationToken);

        if (result is null ||
            string.IsNullOrWhiteSpace(result.DeviceCode) ||
            string.IsNullOrWhiteSpace(result.UserCode) ||
            string.IsNullOrWhiteSpace(result.VerificationUri))
            return null;

        return new GitHubDeviceLogin(
            result.DeviceCode,
            result.UserCode,
            result.VerificationUri,
            result.ExpiresIn,
            result.Interval);
    }

    public async Task<GitHubAccount?> CompleteDeviceLoginAsync(
        GitHubDeviceLogin login,
        string repository,
        CancellationToken cancellationToken = default)
    {
        var interval = Math.Max(login.IntervalSeconds, 5);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(login.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubOAuthSettings.ClientId,
                ["device_code"] = login.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var response = await _http.PostAsync(
                GitHubOAuthSettings.AccessTokenUrl,
                content,
                cancellationToken);

            var payload = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                var account = await GetAccountAsync(
                    payload,
                    repository,
                    cancellationToken);

                if (account is not null)
                    return account;
            }

            if (payload?.Error == "access_denied" ||
                payload?.Error == "expired_token" ||
                payload?.Error == "unsupported_grant_type" ||
                payload?.Error == "incorrect_client_credentials")
                return null;

            if (payload?.Error == "slow_down")
                interval += 5;
        }

        return null;
    }

    public async Task<GitHubAccount?> RefreshAsync(
        GitHubAccount account,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            return account;

        if (account.RefreshTokenExpiresAt is not null &&
            account.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubOAuthSettings.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken
        });

        using var response = await _http.PostAsync(
            GitHubOAuthSettings.AccessTokenUrl,
            content,
            cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            return null;

        account.AccessToken = payload.AccessToken;
        account.RefreshToken = payload.RefreshToken ?? account.RefreshToken;
        account.AccessTokenExpiresAt = payload.ExpiresIn is null
            ? null
            : DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn.Value);
        account.RefreshTokenExpiresAt = payload.RefreshTokenExpiresIn is null
            ? account.RefreshTokenExpiresAt
            : DateTimeOffset.UtcNow.AddSeconds(payload.RefreshTokenExpiresIn.Value);

        return account;
    }

    public void OpenVerificationPage(string verificationUri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = verificationUri,
            UseShellExecute = true
        });
    }

    private async Task<GitHubAccount?> GetAccountAsync(
        OAuthTokenResponse token,
        string repository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GitHubOAuthSettings.ApiBaseUrl}/user");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        var user = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(user?.Login))
            return null;

        return new GitHubAccount
        {
            Login = user.Login,
            Repository = string.IsNullOrWhiteSpace(repository)
                ? "MrLion303/layonevia-memory"
                : repository,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            AccessTokenExpiresAt = token.ExpiresIn is null
                ? null
                : DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn.Value),
            RefreshTokenExpiresAt = token.RefreshTokenExpiresIn is null
                ? null
                : DateTimeOffset.UtcNow.AddSeconds(token.RefreshTokenExpiresIn.Value)
        };
    }

    private sealed class DeviceCodeResponse
    {
        public string? DeviceCode { get; set; }
        public string? UserCode { get; set; }
        public string? VerificationUri { get; set; }
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
    }

    private sealed class OAuthTokenResponse
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int? ExpiresIn { get; set; }
        public int? RefreshTokenExpiresIn { get; set; }
        public string? Error { get; set; }
    }

    private sealed class GitHubUserResponse
    {
        public string? Login { get; set; }
    }
}

public sealed record GitHubDeviceLogin(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);
