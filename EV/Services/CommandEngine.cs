using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace EV.Services;

public sealed class CommandEngine
{
    private readonly MemorySyncService _memory = new();
    private readonly IntentInterpreter _intentInterpreter = new();
    private readonly ConversationContext _context = new();

    public async Task<CommandResult> ExecuteAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var command = text.Trim();

        if (command.Length == 0)
            return CommandResult.Success("Sí, señor.");

        var repeat = TryHandleRepeatCommand(command);
        if (repeat is not null)
            return repeat;

        var confirmation = TryHandlePendingConfirmation(command);
        if (confirmation is not null)
            return confirmation;

        if (TryGetFileActionCommand(command, out var fileAction))
            return ExecuteFileAction(fileAction);

        if (TryGetMemoryRequest(command, out var memory))
        {
            var item = new MemoryItem { Text = memory };
            var result = await _memory.SaveMemoryOnlineAsync(item, cancellationToken);

            return result.State switch
            {
                MemorySyncState.Success => CommandResult.Success(
                    "Listo, señor. Lo recordaré y lo sincronizaré con tu cuenta."),
                MemorySyncState.Offline => CommandResult.Failure(
                    "No puedo guardar recuerdos nuevos mientras estoy sin conexión."),
                MemorySyncState.NotConnected => CommandResult.Failure(
                    "Para guardar recuerdos entre computadoras, primero debes vincular tu cuenta de GitHub."),
                _ => CommandResult.Failure(result.Message)
            };
        }

        var resultReference = _context.ResolveResultReference(command);
        if (resultReference is not null)
        {
            try
            {
                StartShell(resultReference);
                _context.SetOpenedFile(resultReference);
                return CommandResult.Success($"Abriendo «{Path.GetFileName(resultReference)}», señor.");
            }
            catch (Exception ex)
            {
                App.LogException(ex, "No se pudo abrir el resultado seleccionado");
                return CommandResult.Failure("No pude abrir ese resultado.");
            }
        }

        var contextFile = _context.ResolveFileReference(command);
        if (contextFile is not null)
        {
            try
            {
                StartShell(contextFile);
                return CommandResult.Success($"Abriendo «{Path.GetFileName(contextFile)}», señor.");
            }
            catch (Exception ex)
            {
                App.LogException(ex, "No se pudo abrir el archivo del contexto");
                return CommandResult.Failure("No pude abrir el archivo anterior.");
            }
        }

        var contextFolder = _context.ResolveFolderReference(command);
        if (contextFolder is not null)
        {
            try
            {
                StartShell(contextFolder);
                _context.SetOpenedFolder(contextFolder);
                return CommandResult.Success("Volviendo a esa carpeta, señor.");
            }
            catch (Exception ex)
            {
                App.LogException(ex, "No se pudo abrir la carpeta del contexto");
                return CommandResult.Failure("No pude volver a esa carpeta.");
            }
        }

        if (TryGetNavigationCommand(command, out var navigationTarget))
        {
            return ExecuteNavigationCommand(navigationTarget);
        }

        if (TryGetFileCommand(command, out var fileCommand))
        {
            return ExecuteFileCommand(fileCommand);
        }

        if (TryGetApplicationCommand(command, out var appCommand))
            return ExecuteApplicationCommand(appCommand);

        if (TryGetWindowCommand(command, out var windowCommand))
            return ExecuteWindowCommand(windowCommand);

        if (TryGetSystemCommand(command, out var systemCommand))
            return ExecuteSystemCommand(systemCommand);

        if (TryGetWebCommand(command, out var webTarget))
        {
            if (TryOpenWeb(webTarget))
                return CommandResult.Success("Abriendo la búsqueda, señor.");

            return CommandResult.Failure("No pude abrir la búsqueda.");
        }

        if (TryGetFolderCommand(command, out var folderTarget))
        {
            if (TryOpenFolder(folderTarget))
                return CommandResult.Success("Abriendo la carpeta, señor.");

            return CommandResult.Failure("No pude abrir esa ubicación.");
        }

        if (TryGetTypeCommand(command, out var textToType))
        {
            if (TypeText(textToType))
                return CommandResult.Success("Listo, señor.");

            return CommandResult.Failure("No pude escribir el texto en la ventana activa.");
        }

        if (command.Equals("qué puedes hacer", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("que puedes hacer", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Success(
                "Puedo abrir, cerrar, cambiar, minimizar y maximizar aplicaciones, ir al escritorio, escribir texto y controlar algunas funciones de Windows.");
        }

        var conversation = await new ConversationAiService().RespondAsync(command, cancellationToken);
        if (conversation.Succeeded)
            return CommandResult.Success(conversation.Text);

        return CommandResult.Failure(conversation.Text);
    }

    private CommandResult? TryHandleRepeatCommand(string text)
    {
        var normalized = Normalize(text);
        if (!ContainsAny(normalized, "haz lo mismo", "hazlo mismo", "repite eso", "repite la operacion", "repite la operación"))
            return null;

        var action = _context.GetRepeatableAction();
        if (action is null)
            return CommandResult.Failure("No tengo una operación anterior que pueda repetir.");

        if (action.Action == FileActionType.Delete)
            return CommandResult.Failure("No repetiré un borrado automáticamente. Si quieres borrar otro archivo, dímelo explícitamente.");

        var command = new FileActionCommand(action.Action, action.SourcePath, action.DestinationPath, action.NewName);
        return ExecuteFileAction(command);
    }

    private CommandResult? TryHandlePendingConfirmation(string text)
    {
        if (_context.PendingAction is null)
            return null;

        var normalized = Normalize(text);

        if (ContainsAny(normalized, "si", "sí", "confirmo", "hazlo", "adelante", "de acuerdo"))
        {
            var action = _context.TakePendingAction();
            if (action is null)
                return null;

            return ExecuteConfirmedFileAction(action);
        }

        if (ContainsAny(normalized, "no", "cancela", "cancelar", "no lo hagas"))
        {
            _context.TakePendingAction();
            return CommandResult.Success("De acuerdo, no haré ese cambio.");
        }

        return CommandResult.Failure("Tengo una operación pendiente. Responde «sí» para confirmarla o «no» para cancelarla.");
    }

    private bool TryGetFileActionCommand(string text, out FileActionCommand command)
    {
        command = default;
        var normalized = Normalize(text);
        var source = ResolveContextFile(text);

        if (source is null)
            return false;

        if (ContainsAny(normalized, "mueve ", "mover ", "muevelo", "muévelo", "traslada ", "trasladar "))
        {
            var destination = ExtractDestination(normalized);
            if (destination is null)
                return false;

            command = new FileActionCommand(FileActionType.Move, source, destination, null);
            return true;
        }

        if (ContainsAny(normalized, "copia ", "copiar ", "copialo", "cópialo"))
        {
            var destination = ExtractDestination(normalized);
            if (destination is null)
                return false;

            command = new FileActionCommand(FileActionType.Copy, source, destination, null);
            return true;
        }

        if (ContainsAny(normalized, "renombra ", "renombrar ", "cambia el nombre ", "cambiar el nombre "))
        {
            var newName = ExtractNewName(normalized);
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            command = new FileActionCommand(FileActionType.Rename, source, null, newName);
            return true;
        }

        if (ContainsAny(normalized, "borra ", "borrar ", "elimina ", "eliminar ", "eliminalo", "elimínalo", "bórralo"))
        {
            command = new FileActionCommand(FileActionType.Delete, source, null, null);
            return true;
        }

        return false;
    }

    private string? ResolveContextFile(string text)
    {
        var direct = _context.ResolveResultReference(text) ?? _context.ResolveFileReference(text);
        if (direct is not null && File.Exists(direct))
            return direct;

        if (_context.LastFile is not null &&
            ContainsAny(Normalize(text), "ese ", "el archivo", "ese archivo", "el primero", "el segundo"))
            return _context.LastFile;

        return null;
    }

    private static string? ExtractDestination(string normalized)
    {
        foreach (var prefix in new[]
        {
            " al escritorio", " a escritorio",
            " a descargas", " a la carpeta descargas",
            " a documentos", " a la carpeta documentos",
            " a musica", " a la carpeta musica",
            " a imagenes", " a la carpeta imagenes",
            " a videos", " a la carpeta videos"
        })
        {
            if (!normalized.EndsWith(prefix, StringComparison.Ordinal))
                continue;

            return prefix switch
            {
                " al escritorio" or " a escritorio" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                " a descargas" or " a la carpeta descargas" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                " a documentos" or " a la carpeta documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                " a musica" or " a la carpeta musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                " a imagenes" or " a la carpeta imagenes" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                " a videos" or " a la carpeta videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                _ => null
            };
        }

        return null;
    }

    private static string? ExtractNewName(string normalized)
    {
        var patterns = new[]
        {
            @"(?:renombra|renombrar)\s+(?:el\s+)?(?:archivo\s+)?(?:como|a)\s+(?<name>.+)$",
            @"(?:cambia\s+el\s+nombre|cambiar\s+el\s+nombre)\s+(?:de\s+.+?\s+)?(?:a|por)\s+(?<name>.+)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["name"].Value.Trim(' ', '.', ',', ';', ':', '"', '\'');
        }

        return null;
    }

    private CommandResult ExecuteFileAction(FileActionCommand command)
    {
        try
        {
            if (!File.Exists(command.SourcePath))
                return CommandResult.Failure("Ese archivo ya no existe.");

            if (command.Action == FileActionType.Delete)
            {
                _context.SetPendingAction(new PendingFileAction(
                    FileActionType.Delete, command.SourcePath, null, null));

                return CommandResult.Success(
                    $"¿Confirmas que elimine «{Path.GetFileName(command.SourcePath)}»? Responde «sí» o «no».");
            }

            if (command.Action == FileActionType.Rename)
            {
                var destination = Path.Combine(
                    Path.GetDirectoryName(command.SourcePath)!,
                    command.NewName!);

                if (File.Exists(destination))
                    return CommandResult.Failure($"Ya existe un archivo llamado «{command.NewName}».");

                File.Move(command.SourcePath, destination);
                _context.SetLastAction(FileActionType.Rename, destination, null, command.NewName);
                _context.SetOpenedFile(destination);
                _context.SetResults([destination]);

                return CommandResult.Success(
                    $"Listo, renombré el archivo a «{Path.GetFileName(destination)}».");
            }

            if (command.DestinationPath is null || !Directory.Exists(command.DestinationPath))
                return CommandResult.Failure("No encontré la carpeta de destino.");

            var destinationFile = Path.Combine(
                command.DestinationPath,
                Path.GetFileName(command.SourcePath));

            if (File.Exists(destinationFile))
                return CommandResult.Failure(
                    $"Ya existe «{Path.GetFileName(destinationFile)}» en la carpeta de destino.");

            if (command.Action == FileActionType.Move)
                File.Move(command.SourcePath, destinationFile);
            else
                File.Copy(command.SourcePath, destinationFile);

            if (command.Action == FileActionType.Copy)
                _context.SetLastAction(FileActionType.Copy, command.SourcePath, command.DestinationPath, null);
            else
                _context.SetLastAction(FileActionType.Move, destinationFile, command.DestinationPath, null);

            _context.SetOpenedFile(destinationFile);
            _context.SetResults([destinationFile]);

            return CommandResult.Success(
                command.Action == FileActionType.Move
                    ? $"Moví «{Path.GetFileName(destinationFile)}» a «{Path.GetFileName(command.DestinationPath)}»."
                    : $"Copié «{Path.GetFileName(destinationFile)}» a «{Path.GetFileName(command.DestinationPath)}».");
        }
        catch (IOException ex)
        {
            App.LogException(ex, "Error de E/S al manipular un archivo");
            return CommandResult.Failure("Windows no pudo completar esa operación con el archivo.");
        }
        catch (UnauthorizedAccessException ex)
        {
            App.LogException(ex, "Acceso denegado al manipular un archivo");
            return CommandResult.Failure("Windows no me permite modificar ese archivo.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo manipular un archivo");
            return CommandResult.Failure("No pude completar esa operación.");
        }
    }

    private CommandResult ExecuteConfirmedFileAction(PendingFileAction action)
    {
        try
        {
            if (action.Action != FileActionType.Delete)
                return CommandResult.Failure("La confirmación ya no corresponde a una operación válida.");

            if (!File.Exists(action.SourcePath))
                return CommandResult.Failure("Ese archivo ya no existe.");

            File.Delete(action.SourcePath);

            if (_context.LastFile == action.SourcePath)
                _context.SetResults(_context.LastResults.Where(path => !string.Equals(path, action.SourcePath, StringComparison.OrdinalIgnoreCase)));

            return CommandResult.Success($"Listo, eliminé «{Path.GetFileName(action.SourcePath)}».");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo eliminar el archivo confirmado");
            return CommandResult.Failure("No pude eliminar ese archivo.");
        }
    }

    private bool TryGetFileCommand(string text, out FileCommand command)
    {
        command = default;

        if (_intentInterpreter.TryInterpret(text, out var intent) &&
            intent.Action == NaturalAction.OpenFile &&
            !string.IsNullOrWhiteSpace(intent.FileName))
        {
            command = new FileCommand(
                ResolveNaturalLocation(intent.Location),
                intent.FolderName,
                intent.FileName);

            return true;
        }

        var normalized = Normalize(text);
        if (!ContainsAny(normalized, "abre", "abrir", "open", "busca", "buscar", "encuentra", "encuentre"))
            return false;

        var location = ResolveNaturalLocation(normalized);
        var folderName = ExtractNaturalFolder(normalized);
        var fileName = ExtractNaturalFile(normalized);

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        command = new FileCommand(location, folderName, fileName);
        return true;
    }

    private bool TryGetNavigationCommand(string text, out string target)
    {
        target = string.Empty;
        var normalized = Normalize(text);
        var original = text.Trim();

        foreach (var prefix in new[]
        {
            "entra en ",
            "entra a ",
            "ve a la carpeta ",
            "ve a carpeta ",
            "abre la carpeta ",
            "abre carpeta ",
            "abrir la carpeta ",
            "abrir carpeta "
        })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            target = original[prefix.Length..].Trim();
            return target.Length > 0;
        }

        return false;
    }

    private CommandResult ExecuteNavigationCommand(string target)
    {
        try
        {
            var basePath = _context.CurrentPath;

            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                basePath = ResolveNaturalLocation(target);

            var normalizedTarget = Normalize(target);
            var knownPath = normalizedTarget switch
            {
                "escritorio" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "descargas" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                "musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "imagenes" or "fotos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(knownPath) && Directory.Exists(knownPath))
            {
                StartShell(knownPath);
                _context.SetOpenedFolder(knownPath);
                return CommandResult.Success($"Abriendo {target}, señor.");
            }

            var folder = FindDirectory(basePath, target);
            if (folder is null)
                return CommandResult.Failure($"No encontré la carpeta «{target}» dentro de esa ubicación.");

            StartShell(folder);
            _context.SetOpenedFolder(folder);
            return CommandResult.Success($"Entrando en «{Path.GetFileName(folder)}», señor.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo navegar a una carpeta");
            return CommandResult.Failure("No pude abrir esa carpeta.");
        }
    }

    private CommandResult ExecuteFileCommand(FileCommand command)
    {
        try
        {
            var baseFolder = command.Location;
            if (string.IsNullOrWhiteSpace(baseFolder) || !Directory.Exists(baseFolder))
                return CommandResult.Failure("No encontré esa ubicación en el equipo.");

            var searchRoot = baseFolder;

            if (!string.IsNullOrWhiteSpace(command.FolderName))
            {
                var folder = FindDirectory(searchRoot, command.FolderName);
                if (folder is null)
                    return CommandResult.Failure(
                        $"No encontré la carpeta «{command.FolderName}» dentro de {GetLocationName(baseFolder)}.");

                searchRoot = folder;
            }

            var files = FindFiles(searchRoot, command.FileName);
            if (files.Count == 0)
                return CommandResult.Failure(
                    $"No encontré el archivo «{command.FileName}» en esa ubicación.");

            _context.SetResults(files);

            var file = files[0];
            StartShell(file);
            _context.SetOpenedFile(file);

            if (files.Count == 1)
                return CommandResult.Success($"Abriendo «{Path.GetFileName(file)}», señor.");

            var listed = string.Join(", ", files.Take(5).Select((path, index) => $"{index + 1}: {Path.GetFileName(path)}"));
            return CommandResult.Success($"Encontré {files.Count} archivos. El primero es «{Path.GetFileName(file)}». También encontré: {listed}");
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResult.Failure("Encontré la ubicación, pero Windows no me permite acceder a ella.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo resolver una orden de archivo");
            return CommandResult.Failure("No pude abrir ese archivo.");
        }
    }

    private static string ResolveNaturalLocation(string? normalized)
    {
        normalized ??= string.Empty;

        if (normalized.Contains("escritorio", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        if (normalized.Contains("documentos", StringComparison.Ordinal) ||
            normalized.Contains("mis documentos", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (normalized.Contains("descargas", StringComparison.Ordinal) ||
            normalized.Contains("downloads", StringComparison.Ordinal))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

        if (normalized.Contains("musica", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        if (normalized.Contains("imagenes", StringComparison.Ordinal) ||
            normalized.Contains("fotos", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (normalized.Contains("videos", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? ExtractNaturalFolder(string normalized)
    {
        var patterns = new[]
        {
            @"(?:carpeta|directorio|folder)\s+(?<name>.+?)(?=\s+(?:abre|abrir|busca|buscar|encuentra|encuentre)\b|\s+(?:el|un|una)\s+(?:archivo|documento|fichero)\b|$)",
            @"(?:dentro\s+de|entra\s+en|en)\s+(?:la\s+)?(?:carpeta|directorio|folder)\s+(?<name>.+?)(?=\s+(?:abre|abrir|busca|buscar|encuentra|encuentre)\b|\s+(?:el|un|una)\s+(?:archivo|documento|fichero)\b|$)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = CleanNaturalName(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string? ExtractNaturalFile(string normalized)
    {
        var patterns = new[]
        {
            @"(?:abre|abrir|busca|buscar|encuentra|encuentre)\s+(?:el|la|un|una)?\s*(?:archivo|documento|fichero)\s+(?<name>.+?)(?=\s+(?:que|dentro|en|del|de la|de)\b|$)",
            @"(?:archivo|documento|fichero)\s+(?<name>.+?)(?=\s+(?:que|viene|esta|está|dentro|en|del|de la|de)\b|$)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = CleanNaturalName(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string CleanNaturalName(string value)
    {
        return value
            .Trim(' ', '.', ',', ';', ':', '"', '\'')
            .Replace(" que viene", "", StringComparison.Ordinal)
            .Replace(" que esta", "", StringComparison.Ordinal)
            .Replace(" que está", "", StringComparison.Ordinal)
            .Trim();
    }

    private static string? FindDirectory(string root, string requestedName)
    {
        var normalizedRequested = Normalize(Path.GetFileName(requestedName.Trim()));

        if (Normalize(Path.GetFileName(root)) == normalizedRequested)
            return root;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                root,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                }))
            {
                if (Normalize(Path.GetFileName(directory)) == normalizedRequested)
                    return directory;
            }
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo buscar la carpeta {requestedName}");
        }

        return null;
    }

    private static List<string> FindFiles(string root, string requestedName)
    {
        var normalizedRequested = Normalize(requestedName);
        var requestedBase = Normalize(Path.GetFileNameWithoutExtension(requestedName));
        var hasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(requestedName));

        try
        {
            var matches = new List<string>();

            foreach (var file in Directory.EnumerateFiles(
                root,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                }))
            {
                var name = Path.GetFileName(file);
                var normalizedName = Normalize(name);

                if ((hasExtension && normalizedName == normalizedRequested) ||
                    (!hasExtension && Normalize(Path.GetFileNameWithoutExtension(name)) == requestedBase))
                    matches.Add(file);
            }

            return matches
                .OrderBy(path => GetFileMatchPriority(path, hasExtension))
                .ThenBy(path => path.Length)
                .Take(10)
                .ToList();
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo buscar el archivo {requestedName}");
            return [];
        }
    }


    private static int GetFileMatchPriority(string path, bool hasExtension)
    {
        if (hasExtension)
            return 0;

        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".txt" => 0,
            ".doc" or ".docx" => 1,
            ".pdf" => 2,
            ".xlsx" or ".xls" => 3,
            ".pptx" or ".ppt" => 4,
            ".jpg" or ".jpeg" or ".png" or ".webp" => 5,
            ".mp3" or ".wav" or ".flac" => 6,
            ".mp4" or ".mkv" or ".avi" => 7,
            _ => 10
        };
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static bool TryGetMemoryRequest(string text, out string memory)
    {
        memory = string.Empty;
        var normalized = Normalize(text);
        var prefixes = new[]
        {
            "recuerda que ",
            "recuerda ",
            "acuérdate de ",
            "acuerdate de ",
            "no olvides que "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var value = text.Trim()[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            memory = value;
            return true;
        }

        return false;
    }

    private static string GetLocationName(string path)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.Equals(path, desktop, StringComparison.OrdinalIgnoreCase))
            return "el escritorio";

        if (string.Equals(path, documents, StringComparison.OrdinalIgnoreCase))
            return "Documentos";

        return "esa ubicación";
    }

    private static bool TryGetApplicationCommand(string text, out ApplicationCommand command)
    {
        command = default;
        var normalized = Normalize(text);

        var prefixes = new[]
        {
            ("abre ", ApplicationAction.Open),
            ("abrir ", ApplicationAction.Open),
            ("inicia ", ApplicationAction.Open),
            ("iniciar ", ApplicationAction.Open),
            ("cierra ", ApplicationAction.Close),
            ("cerrar ", ApplicationAction.Close),
            ("cambiar a ", ApplicationAction.Switch),
            ("cambia a ", ApplicationAction.Switch),
            ("ve a ", ApplicationAction.Switch),
            ("minimiza ", ApplicationAction.Minimize),
            ("minimizar ", ApplicationAction.Minimize),
            ("maximiza ", ApplicationAction.Maximize),
            ("maximizar ", ApplicationAction.Maximize),
            ("restaura ", ApplicationAction.Restore),
            ("restaurar ", ApplicationAction.Restore)
        };

        foreach (var (prefix, action) in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var target = text.Trim()[prefix.Length..].Trim();
            if (target.Length == 0)
                return false;

            command = new ApplicationCommand(action, target);
            return true;
        }

        if (normalized is "minimiza la ventana" or "minimizar la ventana")
        {
            command = new ApplicationCommand(ApplicationAction.Minimize, "");
            return true;
        }

        if (normalized is "maximiza la ventana" or "maximizar la ventana")
        {
            command = new ApplicationCommand(ApplicationAction.Maximize, "");
            return true;
        }

        if (normalized is "restaura la ventana" or "restaurar la ventana")
        {
            command = new ApplicationCommand(ApplicationAction.Restore, "");
            return true;
        }

        return false;
    }

    private static CommandResult ExecuteApplicationCommand(ApplicationCommand command)
    {
        if (command.Action == ApplicationAction.Open)
        {
            if (TryOpen(command.Target))
                return CommandResult.Success($"Abriendo {command.Target}, señor.");

            return CommandResult.Failure($"No sé cómo abrir {command.Target} todavía.");
        }

        if (command.Action == ApplicationAction.Close)
        {
            if (TryClose(command.Target))
                return CommandResult.Success($"Cerrando {command.Target}, señor.");

            return CommandResult.Failure($"No encontré una aplicación abierta llamada {command.Target}.");
        }

        if (command.Action == ApplicationAction.Switch)
        {
            if (TrySwitch(command.Target))
                return CommandResult.Success($"Cambiando a {command.Target}, señor.");

            return CommandResult.Failure($"No encontré una ventana abierta de {command.Target}.");
        }

        if (command.Action == ApplicationAction.Minimize)
        {
            if (TryMinimize(command.Target))
                return CommandResult.Success("Ventana minimizada, señor.");

            return CommandResult.Failure($"No pude minimizar {DisplayTarget(command.Target)}.");
        }

        if (command.Action == ApplicationAction.Maximize)
        {
            if (TryMaximize(command.Target))
                return CommandResult.Success("Ventana maximizada, señor.");

            return CommandResult.Failure($"No pude maximizar {DisplayTarget(command.Target)}.");
        }

        if (TryRestore(command.Target))
            return CommandResult.Success("Ventana restaurada, señor.");

        return CommandResult.Failure($"No pude restaurar {DisplayTarget(command.Target)}.");
    }

    private static bool TryGetWindowCommand(string text, out WindowCommand command)
    {
        command = default;
        var normalized = Normalize(text);

        if (normalized is "ve al escritorio" or "ir al escritorio" or "muestra el escritorio" or "mostrar el escritorio")
        {
            command = new WindowCommand(WindowAction.Desktop);
            return true;
        }

        if (normalized is "muestra las ventanas" or "mostrar las ventanas" or "cambia de ventana" or "cambiar de ventana")
        {
            command = new WindowCommand(WindowAction.TaskSwitcher);
            return true;
        }

        return false;
    }

    private static CommandResult ExecuteWindowCommand(WindowCommand command)
    {
        try
        {
            if (command.Action == WindowAction.Desktop)
            {
                SendWindowsShortcut(0x5B, 0x44);
                return CommandResult.Success("Listo, señor.");
            }

            SendWindowsShortcut(0x12, 0x09);
            return CommandResult.Success("Cambiando de ventana, señor.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo controlar las ventanas");
            return CommandResult.Failure("No pude controlar las ventanas.");
        }
    }

    private static bool TryGetSystemCommand(string text, out SystemCommand command)
    {
        command = default;
        var normalized = Normalize(text);

        command = normalized switch
        {
            "abre configuracion" or "abrir configuracion" or "abre configuraciones" or "abrir configuraciones"
                => new SystemCommand(SystemAction.Settings),
            "abre el explorador" or "abre explorador" or "abrir el explorador" or "abrir explorador"
                => new SystemCommand(SystemAction.Explorer),
            "abre la calculadora" or "abre calculadora" or "abrir la calculadora" or "abrir calculadora"
                => new SystemCommand(SystemAction.Calculator),
            "abre el bloc de notas" or "abre bloc de notas" or "abrir el bloc de notas" or "abrir bloc de notas"
                => new SystemCommand(SystemAction.Notepad),
            "bloquea el equipo" or "bloquear el equipo" or "bloquea la computadora" or "bloquear la computadora"
                => new SystemCommand(SystemAction.Lock),
            "silencia el volumen" or "silenciar el volumen" or "silencia el sonido" or "silenciar el sonido"
                => new SystemCommand(SystemAction.Mute),
            "sube el volumen" or "subir el volumen"
                => new SystemCommand(SystemAction.VolumeUp),
            "baja el volumen" or "bajar el volumen"
                => new SystemCommand(SystemAction.VolumeDown),
            _ => default
        };

        return command != default;
    }

    private static CommandResult ExecuteSystemCommand(SystemCommand command)
    {
        try
        {
            switch (command.Action)
            {
                case SystemAction.Settings:
                    StartShell("ms-settings:");
                    return CommandResult.Success("Abriendo configuración, señor.");

                case SystemAction.Explorer:
                    StartShell("explorer.exe");
                    return CommandResult.Success("Abriendo el explorador, señor.");

                case SystemAction.Calculator:
                    StartShell("calc.exe");
                    return CommandResult.Success("Abriendo la calculadora, señor.");

                case SystemAction.Notepad:
                    StartShell("notepad.exe");
                    return CommandResult.Success("Abriendo el bloc de notas, señor.");

                case SystemAction.Lock:
                    LockWorkStation();
                    return CommandResult.Success("Bloqueando el equipo, señor.");

                case SystemAction.Mute:
                    SendVirtualKey(0xAD);
                    return CommandResult.Success("Volumen silenciado, señor.");

                case SystemAction.VolumeUp:
                    SendVirtualKey(0xAF);
                    return CommandResult.Success("Subiendo el volumen, señor.");

                case SystemAction.VolumeDown:
                    SendVirtualKey(0xAE);
                    return CommandResult.Success("Bajando el volumen, señor.");
            }
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo ejecutar una acción del sistema");
        }

        return CommandResult.Failure("No pude ejecutar esa acción de Windows.");
    }

    private static bool TryGetWebCommand(string text, out string target)
    {
        target = string.Empty;
        var normalized = Normalize(text);
        var original = text.Trim();

        foreach (var prefix in new[]
        {
            "busca en internet ",
            "busca en google ",
            "busca ",
            "buscar en internet ",
            "buscar en google ",
            "buscar "
        })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            target = original[prefix.Length..].Trim();
            return target.Length > 0;
        }

        return false;
    }

    private static bool TryOpenWeb(string target)
    {
        try
        {
            var url = "https://www.google.com/search?q=" +
                      Uri.EscapeDataString(target);

            StartShell(url);
            return true;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo abrir una búsqueda web");
            return false;
        }
    }

    private static bool TryGetFolderCommand(string text, out string target)
    {
        target = string.Empty;
        var normalized = Normalize(text);
        var original = text.Trim();

        foreach (var prefix in new[]
        {
            "abre la carpeta ",
            "abre carpeta ",
            "abrir la carpeta ",
            "abrir carpeta ",
            "abre la ubicación ",
            "abre ubicacion ",
            "abrir la ubicación ",
            "abrir ubicacion "
        })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            target = original[prefix.Length..].Trim();
            return target.Length > 0;
        }

        return false;
    }

    private static bool TryOpenFolder(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                StartShell(target);
                return true;
            }

            var knownFolder = Normalize(target) switch
            {
                "escritorio" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "descargas" => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads"),
                "musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "imagenes" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(knownFolder) || !Directory.Exists(knownFolder))
                return false;

            StartShell(knownFolder);
            return true;
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo abrir la ubicación {target}");
            return false;
        }
    }

    private static bool TryGetTypeCommand(string text, out string textToType)
    {
        textToType = string.Empty;
        var normalized = Normalize(text);

        foreach (var prefix in new[] { "escribe ", "escribir ", "teclea ", "teclear ", "dicta ", "dictar " })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var original = text.Trim();
            textToType = original[prefix.Length..].Trim();
            return textToType.Length > 0;
        }

        return false;
    }

    private static bool TypeText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            SendWindowsShortcut(0x11, 0x56);
            return true;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo escribir texto");
            return false;
        }
    }

    private static bool TryOpen(string target)
    {
        var normalized = Normalize(target);

        var executable = normalized switch
        {
            "discord" => "discord.exe",
            "chrome" or "google chrome" => "chrome.exe",
            "edge" or "microsoft edge" => "msedge.exe",
            "bloc de notas" or "notas" or "notepad" => "notepad.exe",
            "explorador" or "explorador de archivos" => "explorer.exe",
            "calculadora" => "calc.exe",
            "configuracion" or "configuraciones" => "ms-settings:",
            _ => null
        };

        if (executable is null)
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo abrir {target}");
            return false;
        }
    }

    private static bool TryClose(string target)
    {
        var processName = GetProcessName(target);
        if (processName is null)
            return false;

        var processes = Process.GetProcessesByName(processName);
        var closed = false;

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited && process.CloseMainWindow())
                    closed = true;
            }
            catch (Exception ex)
            {
                App.LogException(ex, $"No se pudo cerrar {target}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return closed;
    }

    private static bool TrySwitch(string target) =>
        FindAndActivateWindow(target);

    private static bool TryMinimize(string target) =>
        FindAndShowWindow(target, 6);

    private static bool TryMaximize(string target) =>
        FindAndShowWindow(target, 3);

    private static bool TryRestore(string target) =>
        FindAndShowWindow(target, 9);

    private static bool FindAndActivateWindow(string target)
    {
        var normalizedTarget = Normalize(target);
        var handle = FindWindowByTarget(normalizedTarget);
        if (handle == IntPtr.Zero)
            return false;

        ShowWindow(handle, 9);
        return SetForegroundWindow(handle);
    }

    private static bool FindAndShowWindow(string target, int command)
    {
        var normalizedTarget = Normalize(target);
        var handle = string.IsNullOrWhiteSpace(normalizedTarget)
            ? GetForegroundWindow()
            : FindWindowByTarget(normalizedTarget);

        if (handle == IntPtr.Zero)
            return false;

        ShowWindow(handle, command);
        return true;
    }

    private static IntPtr FindWindowByTarget(string target)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            var title = GetWindowTitle(handle);
            if (title.Length == 0)
                return true;

            var normalizedTitle = Normalize(title);

            if (normalizedTitle.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                found = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string? GetProcessName(string target)
    {
        return Normalize(target) switch
        {
            "discord" => "Discord",
            "chrome" or "google chrome" => "chrome",
            "edge" or "microsoft edge" => "msedge",
            "bloc de notas" or "notas" or "notepad" => "notepad",
            "explorador" or "explorador de archivos" => "explorer",
            "calculadora" => "CalculatorApp",
            _ => null
        };
    }

    private static void StartShell(string fileName) =>
        Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });

    private static string DisplayTarget(string target) =>
        string.IsNullOrWhiteSpace(target) ? "la ventana activa" : target;

    private static string Normalize(string value)
    {
        return new string(value
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToLowerInvariant()
            .Trim();
    }

    private static void SendVirtualKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
    }

    private static void SendWindowsShortcut(byte firstKey, byte secondKey, byte? thirdKey = null)
    {
        keybd_event(firstKey, 0, 0, UIntPtr.Zero);
        keybd_event(secondKey, 0, 0, UIntPtr.Zero);

        if (thirdKey.HasValue)
            keybd_event(thirdKey.Value, 0, 0, UIntPtr.Zero);

        if (thirdKey.HasValue)
            keybd_event(thirdKey.Value, 0, 2, UIntPtr.Zero);

        keybd_event(secondKey, 0, 2, UIntPtr.Zero);
        keybd_event(firstKey, 0, 2, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maxLength);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr extraData);

    private readonly record struct FileCommand(string Location, string? FolderName, string FileName);
    private readonly record struct FileActionCommand(FileActionType Action, string SourcePath, string? DestinationPath, string? NewName);
    private readonly record struct ApplicationCommand(ApplicationAction Action, string Target);
    private readonly record struct WindowCommand(WindowAction Action);
    private readonly record struct SystemCommand(SystemAction Action);

    private enum ApplicationAction { Open, Close, Switch, Minimize, Maximize, Restore }
    private enum WindowAction { Desktop, TaskSwitcher }
    private enum SystemAction { Settings, Explorer, Calculator, Notepad, Lock, Mute, VolumeUp, VolumeDown }
}

public sealed record CommandResult(bool Succeeded, string Response)
{
    public static CommandResult Success(string response) => new(true, response);
    public static CommandResult Failure(string response) => new(false, response);
}
