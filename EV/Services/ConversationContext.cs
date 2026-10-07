using System.IO;
namespace EV.Services;

public sealed class ConversationContext
{
    private readonly List<string> _lastResults = [];

    public string? CurrentPath { get; private set; }
    public string? LastFile { get; private set; }
    public string? LastFolder { get; private set; }
    public PendingFileAction? PendingAction { get; private set; }

    public IReadOnlyList<string> LastResults => _lastResults;

    public void SetResults(IEnumerable<string> results)
    {
        _lastResults.Clear();
        _lastResults.AddRange(results.Where(File.Exists).Take(10));
    }

    public string? ResolveResultReference(string text)
    {
        var normalized = Normalize(text);

        if (_lastResults.Count == 0)
            return null;

        if (normalized.Contains("primero") || normalized.Contains("primer archivo"))
            return _lastResults[0];

        if (normalized.Contains("segundo") || normalized.Contains("segundo archivo"))
            return _lastResults.Count > 1 ? _lastResults[1] : null;

        if (normalized.Contains("tercero") || normalized.Contains("tercer archivo"))
            return _lastResults.Count > 2 ? _lastResults[2] : null;

        if (normalized.Contains("cuarto") || normalized.Contains("cuarto archivo"))
            return _lastResults.Count > 3 ? _lastResults[3] : null;

        if (normalized.Contains("quinto") || normalized.Contains("quinto archivo"))
            return _lastResults.Count > 4 ? _lastResults[4] : null;

        if (normalized.Contains("el otro") || normalized.Contains("otro archivo"))
            return _lastResults.Count > 1 ? _lastResults[1] : null;

        return null;
    }

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

    public void SetPendingAction(PendingFileAction action) => PendingAction = action;

    public PendingFileAction? TakePendingAction()
    {
        var action = PendingAction;
        PendingAction = null;
        return action;
    }

    public void Clear()
    {
        CurrentPath = null;
        LastFile = null;
        LastFolder = null;
        PendingAction = null;
        _lastResults.Clear();
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

public sealed record PendingFileAction(FileActionType Action, string SourcePath, string? DestinationPath, string? NewName);

public enum FileActionType
{
    Move,
    Copy,
    Rename,
    Delete
}
