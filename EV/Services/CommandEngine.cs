using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

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

        return CommandResult.Failure(
            $"Todavía no tengo una acción para «{command}». Podemos enseñarme esa orden después.");
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
                SendWindowsShortcut(0x12, 0x09, 0x10);
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

        return handle != IntPtr.Zero && ShowWindow(handle, command);
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
