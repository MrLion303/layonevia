namespace EV.Services;

public sealed class GitHubAccount
{
    public string Login { get; set; } = string.Empty;
    public string Repository { get; set; } = "MrLion303/layonevia-memory";
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}