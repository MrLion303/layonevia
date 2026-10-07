using System.Text.Json;

namespace EV.Services;

public sealed class MemorySyncService
{
    private const string MemoryPath = "memory.json";
    private readonly MemoryStore _localStore = new();
    private readonly GitHubMemorySync _github = new();
    private readonly SecureAccountStore _accounts = new();
    private readonly GitHubAuthService _auth = new();

    public GitHubAccount? GetAccount() => _accounts.Load();

    public async Task<MemorySyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var account = _accounts.Load();

        if (account is null)
            return MemorySyncResult.NotConnected("EV todavía no está conectado a GitHub.");

        if (account.AccessTokenExpiresAt is not null &&
            account.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            var refreshed = await _auth.RefreshAsync(account, cancellationToken);

            if (refreshed is null)
            {
                _accounts.Clear();
                return MemorySyncResult.NotConnected("La sesión de GitHub expiró. Vuelve a vincular la cuenta.");
            }

            account = refreshed;
            _accounts.Save(account);
        }

        if (!await _github.CanReachGitHubAsync(cancellationToken))
            return MemorySyncResult.Offline("GitHub no está disponible. Se conserva la última memoria sincronizada.");

        var local = _localStore.Load().ToList();
        var remoteFile = await _github.DownloadMemoryAsync(account.Repository, account.AccessToken, MemoryPath, cancellationToken);
        var remote = remoteFile is null ? new List<MemoryItem>() : Deserialize(remoteFile.Content);
        var merged = Merge(local, remote);

        var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });

        var saved = await _github.SaveMemoryAsync(
            account.Repository,
            account.AccessToken,
            MemoryPath,
            json,
            remoteFile?.Sha,
            cancellationToken);

        if (!saved)
            return MemorySyncResult.Failed("No se pudo actualizar la memoria en GitHub.");

        _localStore.Save(merged);
        return MemorySyncResult.Success(merged.Count);
    }

    public void SaveLocal(MemoryItem memory)
    {
        var memories = _localStore.Load().ToList();
        memories.RemoveAll(x => x.Id == memory.Id);
        memories.Add(memory);
        _localStore.Save(memories);
    }

    private static List<MemoryItem> Deserialize(string content)
    {
        try { return JsonSerializer.Deserialize<List<MemoryItem>>(content) ?? []; }
        catch { return []; }
    }

    private static List<MemoryItem> Merge(IEnumerable<MemoryItem> local, IEnumerable<MemoryItem> remote)
    {
        var result = new Dictionary<string, MemoryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in local.Concat(remote))
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Text))
                continue;

            if (!result.TryGetValue(item.Id, out var existing) || item.UpdatedAt > existing.UpdatedAt)
                result[item.Id] = item;
        }

        return result.Values.OrderBy(x => x.CreatedAt).ToList();
    }
}

public enum MemorySyncState { Success, Offline, NotConnected, Failed }

public sealed record MemorySyncResult(MemorySyncState State, string Message, int MemoryCount = 0)
{
    public static MemorySyncResult Success(int count) => new(MemorySyncState.Success, "Memoria sincronizada correctamente.", count);
    public static MemorySyncResult Offline(string message) => new(MemorySyncState.Offline, message);
    public static MemorySyncResult NotConnected(string message) => new(MemorySyncState.NotConnected, message);
    public static MemorySyncResult Failed(string message) => new(MemorySyncState.Failed, message);
}