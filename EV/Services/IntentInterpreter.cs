using System.Text.RegularExpressions;

namespace EV.Services;

public sealed class IntentInterpreter
{
    public bool TryInterpret(string text, out NaturalIntent intent)
    {
        intent = default;
        var normalized = Normalize(text);

        if (normalized.Length == 0)
            return false;

        var action = DetectAction(normalized);
        if (action == NaturalAction.Unknown)
            return false;

        var location = DetectLocation(normalized);
        var folder = ExtractFolder(normalized);
        var file = ExtractFile(normalized);
        var target = ExtractTarget(normalized);

        if (action == NaturalAction.OpenFile && string.IsNullOrWhiteSpace(file))
            return false;

        intent = new NaturalIntent(action, location, folder, file, target);
        return true;
    }

    private static NaturalAction DetectAction(string text)
    {
        if (ContainsAny(text, "abre", "abrir", "abreme", "abrirme", "abre el", "abre la") &&
            ContainsAny(text, "archivo", "documento", "fichero"))
            return NaturalAction.OpenFile;

        if (ContainsAny(text, "abre", "abrir", "abreme", "abrirme") &&
            (ContainsAny(text, "carpeta", "directorio") || ContainsAny(text, "escritorio", "documentos", "descargas")))
            return NaturalAction.OpenLocation;

        return NaturalAction.Unknown;
    }

    private static string? DetectLocation(string text)
    {
        if (ContainsAny(text, "escritorio"))
            return "escritorio";
        if (ContainsAny(text, "documentos", "mis documentos"))
            return "documentos";
        if (ContainsAny(text, "descargas", "downloads"))
            return "descargas";
        if (ContainsAny(text, "musica"))
            return "musica";
        if (ContainsAny(text, "imagenes", "fotos"))
            return "imagenes";
        if (ContainsAny(text, "videos"))
            return "videos";
        return null;
    }

    private static string? ExtractFolder(string text)
    {
        var patterns = new[]
        {
            @"(?:la\s+|una\s+)?carpeta\s+(?<name>.+?)(?=\s+(?:abre|abrir|abreme|abrirme)\b|\s+(?:el|la|un|una)\s+(?:archivo|documento|fichero)\b|$)",
            @"(?:dentro\s+de|entra\s+en)\s+(?:la\s+)?carpeta\s+(?<name>.+?)(?=\s+(?:abre|abrir)\b|\s+(?:el|la|un|una)\s+(?:archivo|documento|fichero)\b|$)"
        };

        return ExtractName(patterns, text);
    }

    private static string? ExtractFile(string text)
    {
        var patterns = new[]
        {
            @"(?:archivo|documento|fichero)\s+(?<name>.+?)(?=\s+(?:que|viene|esta|está|se\s+encuentra|dentro|en|del|de\s+la|de)\b|$)",
            @"(?:abre|abrir)\s+(?:el|la|un|una)?\s*(?:archivo|documento|fichero)\s+(?<name>.+?)(?=\s+(?:que|viene|esta|está|dentro|en|del|de\s+la|de)\b|$)"
        };

        return ExtractName(patterns, text);
    }

    private static string? ExtractTarget(string text)
    {
        var match = Regex.Match(text, @"(?:abre|abrir)\s+(?<target>.+)$", RegexOptions.IgnoreCase);
        return match.Success ? CleanName(match.Groups["target"].Value) : null;
    }

    private static string? ExtractName(IEnumerable<string> patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = CleanName(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string CleanName(string value)
    {
        return value
            .Trim(' ', '.', ',', ';', ':', '"', '\'')
            .Replace(" que viene", "", StringComparison.Ordinal)
            .Replace(" que esta", "", StringComparison.Ordinal)
            .Replace(" que está", "", StringComparison.Ordinal)
            .Trim();
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

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

public readonly record struct NaturalIntent(
    NaturalAction Action,
    string? Location,
    string? FolderName,
    string? FileName,
    string? Target);

public enum NaturalAction
{
    Unknown,
    OpenFile,
    OpenLocation
}
