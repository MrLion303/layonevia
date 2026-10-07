using System.Security.Cryptography;
using System.Text;

namespace EV.Services;

public sealed class SecureAccountStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("EV-GitHub-Account-v1");

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EV",
        "account.dat");

    public GitHubAccount? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var encrypted = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);

            return System.Text.Json.JsonSerializer.Deserialize<GitHubAccount>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(GitHubAccount account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(account);
        var encrypted = ProtectedData.Protect(
            json,
            Entropy,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(_path, encrypted);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
        }
    }
}