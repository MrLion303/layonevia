namespace EV.Services;

public sealed class ConversationContext
{
    public string? CurrentPath { get; private set; }
    public string? LastFile { get; private set; }
    public string? LastFolder { get; private set; }

    public void SetOpenedFile(string path)
    {
        LastFile = path;
        if (File.Exists(path))
            CurrentPath = Path.GetDirectoryName(path);
    }

    public void SetOpenedFolder(string path)
    {
        LastFolder = path;
        CurrentPath = path;
    }

    public void SetCurrentPath(string path)
    {
        if (Directory.Exists(path))
            CurrentPath = path;
    }

    public string? ResolveFileReference(string text)
    {
        var normalized = Normalize(text);

        if (normalized.Contains("ese archivo") ||
            normalized.Contains("ese documento") ||
            normalized.Contains("ese fichero") ||
            normalized is "abre ese" or "abrir ese" or "abrelo" or "abrirlo")
            return LastFile;

        return null;
    }

    public string? ResolveFolderReference(string text)
    {
        var normalized = Normalize(text);

        if (normalized.Contains("esa carpeta") ||
            normalized.Contains("esa ubicacion") ||
            normalized.Contains("esa ubicación") ||
            normalized is "vuelve ahi" or "volver ahi")
            return LastFolder ?? CurrentPath;

        return null;
    }

    public void Clear()
    {
        CurrentPath = null;
        LastFile = null;
        LastFolder = null;
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToLowerInvariant()
            .Trim();
    }
}
