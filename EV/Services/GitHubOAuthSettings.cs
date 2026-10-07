namespace EV.Services;

public static class GitHubOAuthSettings
{
    // Se sustituirá por el Client ID de la aplicación OAuth de EV.
    // El Client ID no es un secreto y puede formar parte del ejecutable.
    public const string ClientId = "PON_AQUI_EL_CLIENT_ID_DE_EV";
    public const string Scope = "repo offline_access";
    public const string DeviceCodeUrl = "https://github.com/login/device/code";
    public const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    public const string ApiBaseUrl = "https://api.github.com";
}