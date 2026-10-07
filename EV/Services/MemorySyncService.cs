using System.Net;
using System.Net.Http;
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

    public IReadOnlyList<MemoryItem> LoadLocalMemory() => _localStore.Load();

    public async Task<MemorySyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        try
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
                    return MemorySyncResult.NotConnected(
                        "La sesión de GitHub expiró. Vuelve a vincular la cuenta.");
                }

                account = refreshed;
                _accounts.Save(account);
            }

            if (!await _github.CanReachGitHubAsync(cancellationToken))
                return MemorySyncResult.Offline(
                    "GitHub no está disponible. Se conserva la última memoria sincronizada.");

            var local = _localStore.Load().ToList();
            var remoteFile = await _github.DownloadMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                cancellationToken);

            var remote = remoteFile is null
                ? new List<MemoryItem>()
                : Deserialize(remoteFile.Content);

            var merged = Merge(local, remote);
            var json = JsonSerializer.Serialize(
                merged,
                new JsonSerializerOptions { WriteIndented = true });

            var saved = await _github.SaveMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                json,
                remoteFile?.Sha,
                cancellationToken);

            if (!saved)
                return MemorySyncResult.Failed(
                    "No se pudo actualizar la memoria en GitHub. No se modificó la copia local.");

            _localStore.Save(merged);
            return MemorySyncResult.Success(merged.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            App.LogException(ex, "Error HTTP al sincronizar la memoria");
            return MemorySyncResult.Failed(
                "GitHub rechazó la sincronización. La memoria local no fue modificada.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error inesperado al sincronizar la memoria");
            return MemorySyncResult.Failed(
                "La sincronización falló. La memoria local no fue modificada.");
        }
    }

    public async Task<MemorySyncResult> UpsertMemoryOnlineAsync(
        MemoryItem memory,
        Func<MemoryItem, bool> matches,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var account = _accounts.Load();
            if (account is null)
                return MemorySyncResult.NotConnected("EV todavía no está conectado a GitHub.");

            if (account.AccessTokenExpiresAt is not null &&
                account.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refreshed = await _auth.RefreshAsync(account, cancellationToken);
                if (refreshed is null)
                    return MemorySyncResult.NotConnected("La sesión de GitHub expiró. Vuelve a vincular la cuenta.");

                account = refreshed;
                _accounts.Save(account);
            }

            if (!await _github.CanReachGitHubAsync(cancellationToken))
                return MemorySyncResult.Offline("GitHub no está disponible.");

            var local = _localStore.Load().ToList();
            var remoteFile = await _github.DownloadMemoryAsync(
                account.Repository, account.AccessToken, MemoryPath, cancellationToken);

            var remote = remoteFile is null ? new List<MemoryItem>() : Deserialize(remoteFile.Content);
            var merged = Merge(local, remote);

            var existing = merged
                .Where(matches)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (existing is not null)
            {
                memory.Id = existing.Id;
                memory.CreatedAt = existing.CreatedAt;
            }

            memory.UpdatedAt = DateTimeOffset.UtcNow;

            merged.RemoveAll(x => x.Id == memory.Id);
            merged.Add(memory);

            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            var saved = await _github.SaveMemoryAsync(
                account.Repository, account.AccessToken, MemoryPath, json,
                remoteFile?.Sha, cancellationToken);

            if (!saved)
                return MemorySyncResult.Failed("No se pudo actualizar el recuerdo en GitHub.");

            _localStore.Save(merged);
            return MemorySyncResult.Success(merged.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al actualizar un recuerdo");
            return MemorySyncResult.Failed("No se pudo actualizar el recuerdo.");
        }
    }

    public async Task<MemorySyncResult> SaveMemoryOnlineAsync(
        MemoryItem memory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var account = _accounts.Load();

            if (account is null)
                return MemorySyncResult.NotConnected(
                    "EV todavía no está conectado a GitHub.");

            if (account.AccessTokenExpiresAt is not null &&
                account.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refreshed = await _auth.RefreshAsync(account, cancellationToken);

                if (refreshed is null)
                    return MemorySyncResult.NotConnected(
                        "La sesión de GitHub expiró. Vuelve a vincular la cuenta.");

                account = refreshed;
                _accounts.Save(account);
            }

            if (!await _github.CanReachGitHubAsync(cancellationToken))
                return MemorySyncResult.Offline(
                    "GitHub no está disponible.");

            var local = _localStore.Load().ToList();
            var remoteFile = await _github.DownloadMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                cancellationToken);

            var remote = remoteFile is null
                ? new List<MemoryItem>()
                : Deserialize(remoteFile.Content);

            var merged = Merge(local.Concat(new[] { memory }), remote);
            var json = JsonSerializer.Serialize(
                merged,
                new JsonSerializerOptions { WriteIndented = true });

            var saved = await _github.SaveMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                json,
                remoteFile?.Sha,
                cancellationToken);

            if (!saved)
                return MemorySyncResult.Failed(
                    "No se pudo guardar el recuerdo en GitHub.");

            _localStore.Save(merged);
            return MemorySyncResult.Success(merged.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            App.LogException(ex, "Error HTTP al guardar un recuerdo");
            return MemorySyncResult.Failed(
                "GitHub no pudo guardar el recuerdo.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al guardar un recuerdo");
            return MemorySyncResult.Failed(
                "No se pudo guardar el recuerdo.");
        }
    }

    public void SaveLocal(MemoryItem memory)
    {
        var memories = _localStore.Load().ToList();
        memories.RemoveAll(x => x.Id == memory.Id);
        memories.Add(memory);
        _localStore.Save(memories);
    }

    public async Task<(MemorySyncResult Result, int Removed)> ForgetMemoryOnlineAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (MemorySyncResult.Failed("No se indicó qué recuerdo eliminar."), 0);

        try
        {
            var account = _accounts.Load();

            if (account is null)
                return (MemorySyncResult.NotConnected(
                    "EV todavía no está conectado a GitHub."), 0);

            if (account.AccessTokenExpiresAt is not null &&
                account.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refreshed = await _auth.RefreshAsync(account, cancellationToken);

                if (refreshed is null)
                    return (MemorySyncResult.NotConnected(
                        "La sesión de GitHub expiró. Vuelve a vincular la cuenta."), 0);

                account = refreshed;
                _accounts.Save(account);
            }

            if (!await _github.CanReachGitHubAsync(cancellationToken))
                return (MemorySyncResult.Offline(
                    "GitHub no está disponible. No se eliminó el recuerdo."), 0);

            var local = _localStore.Load().ToList();
            var remoteFile = await _github.DownloadMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                cancellationToken);

            var remote = remoteFile is null
                ? new List<MemoryItem>()
                : Deserialize(remoteFile.Content);

            var merged = Merge(local, remote);
            var normalizedQuery = NormalizeMemoryText(query);
            var removed = merged.RemoveAll(item =>
                NormalizeMemoryText(item.Text).Equals(normalizedQuery, StringComparison.Ordinal) ||
                NormalizeMemoryText(item.Text).Contains(normalizedQuery, StringComparison.Ordinal));

            if (removed == 0)
                return (MemorySyncResult.Success(merged.Count), 0);

            var json = JsonSerializer.Serialize(
                merged,
                new JsonSerializerOptions { WriteIndented = true });

            var saved = await _github.SaveMemoryAsync(
                account.Repository,
                account.AccessToken,
                MemoryPath,
                json,
                remoteFile?.Sha,
                cancellationToken);

            if (!saved)
                return (MemorySyncResult.Failed(
                    "No se pudo actualizar la memoria en GitHub. El recuerdo sigue guardado."), 0);

            _localStore.Save(merged);
            return (MemorySyncResult.Success(merged.Count), removed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error al olvidar un recuerdo");
            return (MemorySyncResult.Failed(
                "No se pudo eliminar el recuerdo."), 0);
        }
    }

    private static string NormalizeMemoryText(string value)
    {
        return new string(value
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray())
            .Trim()
            .ToLowerInvariant();
    }

    private static List<MemoryItem> Deserialize(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<List<MemoryItem>>(content) ?? [];
        }
        catch (JsonException ex)
        {
            App.LogException(ex, "La memoria remota contiene JSON inválido");
            return [];
        }
    }

    private static List<MemoryItem> Merge(
        IEnumerable<MemoryItem> local,
        IEnumerable<MemoryItem> remote)
    {
        var result = new Dictionary<string, MemoryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in local.Concat(remote))
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Text))
                continue;

            if (!result.TryGetValue(item.Id, out var existing) ||
                item.UpdatedAt > existing.UpdatedAt)
            {
                result[item.Id] = item;
            }
        }

        return result.Values.OrderBy(x => x.CreatedAt).ToList();
    }
}

public enum MemorySyncState { Success, Offline, NotConnected, Failed }

public sealed record MemorySyncResult(
    MemorySyncState State,
    string Message,
    int MemoryCount = 0)
{
    public static MemorySyncResult Success(int count) =>
        new(MemorySyncState.Success, "Memoria sincronizada correctamente.", count);

    public static MemorySyncResult Offline(string message) =>
        new(MemorySyncState.Offline, message);

    public static MemorySyncResult NotConnected(string message) =>
        new(MemorySyncState.NotConnected, message);

    public static MemorySyncResult Failed(string message) =>
        new(MemorySyncState.Failed, message);
}
