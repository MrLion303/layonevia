using System.Diagnostics;

namespace EV.Services;

public sealed class CommandEngine
{
    private readonly MemorySyncService _memory = new();

    public async Task<CommandResult> ExecuteAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var command = text.Trim();

        if (command.Length == 0)
            return CommandResult.Success("Sí, señor.");

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

        if (StartsWith(command, "abre ", "abrir "))
        {
            var target = command[(command.IndexOf(' ') + 1)..].Trim();
            if (TryOpen(target))
                return CommandResult.Success($"Abriendo {target}, señor.");

            return CommandResult.Failure($"No sé cómo abrir {target} todavía.");
        }

        if (StartsWith(command, "cierra ", "cerrar "))
        {
            var target = command[(command.IndexOf(' ') + 1)..].Trim();
            if (TryClose(target))
                return CommandResult.Success($"Cerrando {target}, señor.");

            return CommandResult.Failure($"No encontré una aplicación abierta llamada {target}.");
        }

        if (command.Equals("qué puedes hacer", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("que puedes hacer", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Success(
                "Por ahora puedo guardar recuerdos sincronizados y abrir o cerrar algunas aplicaciones de Windows.");
        }

        return CommandResult.Failure(
            $"Todavía no tengo una acción para «{command}». Podemos enseñarme esa orden después.");
    }

    public bool TryGetMemoryRequest(string text, out string memory)
    {
        memory = "";
        var normalized = text.Trim();
        var prefixes = new[]
        {
            "recuerda que ",
            "recuerda ",
            "quiero que recuerdes que ",
            "necesito que recuerdes que "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            memory = normalized[prefix.Length..].Trim();
            return memory.Length > 0;
        }

        return false;
    }

    private static bool StartsWith(string text, params string[] prefixes) =>
        prefixes.Any(x => text.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    private static bool TryOpen(string target)
    {
        var normalized = target.Trim().ToLowerInvariant();

        var executable = normalized switch
        {
            "discord" => "discord.exe",
            "chrome" or "google chrome" => "chrome.exe",
            "edge" or "microsoft edge" => "msedge.exe",
            "bloc de notas" or "notas" or "notepad" => "notepad.exe",
            "explorador" or "explorador de archivos" => "explorer.exe",
            "calculadora" => "calc.exe",
            "configuración" or "configuracion" => "ms-settings:",
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
        var normalized = target.Trim().ToLowerInvariant();

        var processName = normalized switch
        {
            "discord" => "Discord",
            "chrome" or "google chrome" => "chrome",
            "edge" or "microsoft edge" => "msedge",
            "bloc de notas" or "notas" or "notepad" => "notepad",
            "explorador" or "explorador de archivos" => "explorer",
            "calculadora" => "CalculatorApp",
            _ => null
        };

        if (processName is null)
            return false;

        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
            return false;

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
}

public sealed record CommandResult(bool Success, string Response)
{
    public static CommandResult Success(string response) => new(true, response);
    public static CommandResult Failure(string response) => new(false, response);
}
