namespace EV.Services;

public sealed class ConversationContext
{
    public string? CurrentLocation { get; private set; }
    public string? CurrentFolder { get; private set; }
    public string? LastFile { get; private set; }
    public string? LastFolder { get; private set; }

    public void Update(NaturalIntent intent)
    {
        if (!string.IsNullOrWhiteSpace(intent.Location))
            CurrentLocation = intent.Location;

        if (!string.IsNullOrWhiteSpace(intent.FolderName))
        {
            CurrentFolder = intent.FolderName;
            LastFolder = intent.FolderName;
        }

        if (!string.IsNullOrWhiteSpace(intent.FileName))
            LastFile = intent.FileName;
    }

    public void SetOpenedFile(string path)
    {
        LastFile = path;
    }

    public string? ResolveFileReference(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Contains("ese archivo") || normalized.Contains("ese documento") ||
            normalized.Contains("ese fichero"))
            return LastFile;

        return null;
    }

    public void SetOpenedFolder(string path)
    {
        LastFolder = path;
        CurrentFolder = path;
    }

    public string? ResolvePronoun(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();

        if (normalized.Contains("ese archivo") || normalized.Contains("ese documento") ||
            normalized.Contains("ese fichero") || normalized.Contains("ese"))
            return LastFile;

        if (normalized.Contains("esa carpeta") || normalized.Contains("esa") ||
            normalized.Contains("esa ubicacion") || normalized.Contains("esa ubicación"))
            return LastFolder ?? CurrentLocation;

        return null;
    }

    public void Clear()
    {
        CurrentLocation = null;
        CurrentFolder = null;
        LastFile = null;
        LastFolder = null;
    }
}
