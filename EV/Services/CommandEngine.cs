using System.Text.Json;
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
    private readonly RoutineStore _routines = new();
    private readonly ConversationAiService _conversation;
    private bool _runningRoutine;

    public CommandEngine()
    {
        _conversation = new ConversationAiService(ExecuteAiToolAsync);
    }

    public async Task<AiToolResult> ExecuteAiToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var arguments = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var root = arguments.RootElement;

            switch (toolName)
            {
                case "get_current_context":
                    return ExecuteAiContextTool();

                case "get_system_state":
                    return ExecuteAiSystemStateTool();

                case "list_open_windows":
                    return ExecuteAiListOpenWindowsTool();

                case "get_system_info":
                    return ExecuteAiSystemInfoTool();

                case "list_running_apps":
                    return ExecuteAiRunningAppsTool();

                case "clipboard":
                    return ExecuteAiClipboardTool(root);

                case "get_preferences":
                    return ExecuteAiPreferencesTool(root);

                case "run_routine":
                    return await ExecuteAiRoutineToolAsync(root, cancellationToken);

                case "create_routine":
                    return ExecuteAiCreateRoutineTool(root);

                case "create_routine_from_task":
                    return ExecuteAiCreateRoutineFromTaskTool(root);

                case "modify_routine_step":
                    return ExecuteAiModifyRoutineStepTool(root);

                case "list_routines":
                    return ExecuteAiListRoutinesTool();

                case "delete_routine":
                    return ExecuteAiDeleteRoutineTool(root);

                case "inspect_routine":
                    return ExecuteAiInspectRoutineTool(root);

                case "edit_routine":
                    return ExecuteAiEditRoutineTool(root);

                case "open_application":
                    return RecordToolResult(toolName, argumentsJson, $"Abrir aplicación «{GetToolString(root, "application")}»", ExecuteAiApplicationTool(root, ApplicationAction.Open));

                case "close_application":
                    return RecordToolResult(toolName, argumentsJson, $"Cerrar aplicación «{GetToolString(root, "application")}»", ExecuteAiApplicationTool(root, ApplicationAction.Close));

                case "control_window":
                    return RecordToolResult(toolName, argumentsJson, $"Controlar ventana ({GetToolString(root, "action")})", ExecuteAiWindowTool(root));

                case "system_action":
                    return RecordToolResult(toolName, argumentsJson, $"Acción del sistema «{GetToolString(root, "action")}»", ExecuteAiSystemTool(root));

                case "search_web":
                    if (!root.TryGetProperty("query", out var query) ||
                        string.IsNullOrWhiteSpace(query.GetString()))
                        return AiToolResult.Failure("No se indicó qué buscar.");

                    var webResult = TryOpenWeb(query.GetString()!)
                        ? AiToolResult.Success($"Búsqueda abierta para «{query.GetString()}».")
                        : AiToolResult.Failure("No pude abrir la búsqueda.");
                    return RecordToolResult(toolName, argumentsJson, $"Buscar en Internet «{query.GetString()}»", webResult);

                case "open_folder":
                    if (!root.TryGetProperty("folder", out var folder) ||
                        string.IsNullOrWhiteSpace(folder.GetString()))
                        return AiToolResult.Failure("No se indicó la carpeta.");

                    var folderResult = TryOpenFolder(folder.GetString()!)
                        ? AiToolResult.Success($"Carpeta abierta: {folder.GetString()}.")
                        : AiToolResult.Failure($"No pude abrir la carpeta «{folder.GetString()}».");
                    return RecordToolResult(toolName, argumentsJson, $"Abrir carpeta «{folder.GetString()}»", folderResult);

                case "type_text":
                    if (!root.TryGetProperty("text", out var text) ||
                        string.IsNullOrEmpty(text.GetString()))
                        return AiToolResult.Failure("No se indicó texto para escribir.");

                    var typeResult = TypeText(text.GetString()!)
                        ? AiToolResult.Success("El texto fue escrito en la ventana activa.")
                        : AiToolResult.Failure("No pude escribir el texto en la ventana activa.");
                    return RecordToolResult(toolName, argumentsJson, "Escribir texto", typeResult);

                case "find_file":
                    return RecordToolResult(toolName, argumentsJson, "Buscar archivo", ExecuteAiFindFileTool(root));

                case "open_file":
                    return RecordToolResult(toolName, argumentsJson, "Abrir archivo", ExecuteAiOpenFileTool(root));

                case "file_action":
                    return RecordToolResult(toolName, argumentsJson, "Operación de archivo", ExecuteAiFileActionTool(root));

                default:
                    return AiToolResult.Failure($"Herramienta desconocida: {toolName}.");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return AiToolResult.Failure("Los argumentos de la herramienta no eran válidos.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"Error ejecutando herramienta de IA {toolName}");
            return AiToolResult.Failure("Windows no pudo completar esa acción.");
        }
    }

    private AiToolResult RecordToolResult(
        string toolName,
        string argumentsJson,
        string action,
        AiToolResult result)
    {
        _context.RecordToolAction(action, result.Message);

        if (result.Succeeded && !_runningRoutine)
            _context.RecordExecutedTool(toolName, argumentsJson, action, result.Message);

        if (_context.CurrentTask is not null)
        {
            if (result.Succeeded)
                _context.SetTaskStep(action, _context.CurrentTask.StepNumber + 1, _context.CurrentTask.TotalSteps);
            else
                _context.FailTask(result.Message);
        }

        return result;
    }

    private AiToolResult ExecuteAiCreateRoutineFromTaskTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        var description = GetToolString(root, "description").Trim();

        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        if (_routines.Find(name) is not null)
            return AiToolResult.Failure($"Ya existe una rutina llamada «{name}».");

        var sequence = _context.RecentToolSequence;
        if (sequence.Count < 2)
            return AiToolResult.Failure("No hay una secuencia reciente con suficientes acciones para convertirla en rutina.");

        var steps = sequence
            .Where(x => IsRoutineStepAllowed(x.ToolName))
            .Select(x => new RoutineStep
            {
                ToolName = x.ToolName,
                ArgumentsJson = x.ArgumentsJson
            })
            .ToList();

        if (steps.Count < 2)
            return AiToolResult.Failure("La secuencia reciente no contiene suficientes acciones compatibles con rutinas.");

        var routine = new EvRoutine
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(description)
                ? $"Rutina creada a partir de la última secuencia de acciones de EV."
                : description,
            Steps = steps
        };

        var routines = _routines.Load().ToList();
        routines.Add(routine);
        _routines.Save(routines);
        _context.ClearRecentToolSequence();

        return AiToolResult.Success($"Rutina «{name}» creada a partir de la secuencia reciente con {steps.Count} pasos.");
    }

    private AiToolResult ExecuteAiModifyRoutineStepTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        var action = GetToolString(root, "action").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        var routine = _routines.Find(name);
        if (routine is null)
            return AiToolResult.Failure($"No existe una rutina llamada «{name}».");

        if (routine.Steps.Count == 0 && action != "add")
            return AiToolResult.Failure("La rutina no tiene pasos que modificar.");

        if (!root.TryGetProperty("index", out var indexElement) ||
            !indexElement.TryGetInt32(out var index))
            index = -1;

        switch (action)
        {
            case "add":
            {
                if (!root.TryGetProperty("step", out var stepElement) ||
                    stepElement.ValueKind != JsonValueKind.Object)
                    return AiToolResult.Failure("Para añadir un paso debes indicar step.");

                var step = ParseRoutineStep(stepElement);
                if (step is null)
                    return AiToolResult.Failure("El nuevo paso no es válido.");

                var insertAt = index < 0 ? routine.Steps.Count : index;
                if (insertAt < 0 || insertAt > routine.Steps.Count)
                    return AiToolResult.Failure("La posición del nuevo paso no es válida.");

                routine.Steps.Insert(insertAt, step);
                break;
            }

            case "remove":
                if (index < 0 || index >= routine.Steps.Count)
                    return AiToolResult.Failure("El índice del paso que quieres eliminar no es válido.");

                routine.Steps.RemoveAt(index);
                break;

            case "move":
            {
                if (index < 0 || index >= routine.Steps.Count)
                    return AiToolResult.Failure("El índice del paso que quieres mover no es válido.");

                if (!root.TryGetProperty("new_index", out var newIndexElement) ||
                    !newIndexElement.TryGetInt32(out var newIndex) ||
                    newIndex < 0 || newIndex >= routine.Steps.Count)
                    return AiToolResult.Failure("La nueva posición no es válida.");

                var step = routine.Steps[index];
                routine.Steps.RemoveAt(index);
                routine.Steps.Insert(newIndex, step);
                break;
            }

            case "replace":
            {
                if (index < 0 || index >= routine.Steps.Count)
                    return AiToolResult.Failure("El índice del paso que quieres reemplazar no es válido.");

                if (!root.TryGetProperty("step", out var stepElement) ||
                    stepElement.ValueKind != JsonValueKind.Object)
                    return AiToolResult.Failure("Para reemplazar un paso debes indicar step.");

                var step = ParseRoutineStep(stepElement);
                if (step is null)
                    return AiToolResult.Failure("El nuevo paso no es válido.");

                routine.Steps[index] = step;
                break;
            }

            default:
                return AiToolResult.Failure("La acción debe ser add, remove, move o replace.");
        }

        if (routine.Steps.Count == 0)
            return AiToolResult.Failure("Una rutina no puede quedarse sin pasos.");

        return _routines.Update(routine)
            ? AiToolResult.Success($"Rutina «{routine.Name}» modificada. Ahora tiene {routine.Steps.Count} pasos.")
            : AiToolResult.Failure("No pude guardar los cambios de la rutina.");
    }

    private static RoutineStep? ParseRoutineStep(JsonElement element)
    {
        if (!element.TryGetProperty("tool", out var toolElement) ||
            !element.TryGetProperty("arguments", out var argumentsElement))
            return null;

        var tool = toolElement.GetString()?.Trim();
        var arguments = argumentsElement.GetString() ?? "{}";

        if (string.IsNullOrWhiteSpace(tool) || !IsRoutineStepAllowed(tool))
            return null;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        return new RoutineStep
        {
            ToolName = tool,
            ArgumentsJson = arguments
        };
    }

    private static bool IsRoutineStepAllowed(string toolName) =>
        toolName is
            "open_application" or "close_application" or "control_window" or
            "system_action" or "search_web" or "open_folder" or "type_text" or
            "find_file" or "open_file" or "file_action";

    private AiToolResult ExecuteAiCreateRoutineTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        var description = GetToolString(root, "description").Trim();

        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
            return AiToolResult.Failure("La rutina necesita al menos un paso.");

        if (_routines.Find(name) is not null)
            return AiToolResult.Failure($"Ya existe una rutina llamada «{name}».");

        var routine = new EvRoutine
        {
            Name = name,
            Description = description,
            Steps = steps.EnumerateArray()
                .Select(step => new RoutineStep
                {
                    ToolName = step.GetProperty("tool").GetString() ?? "",
                    ArgumentsJson = step.GetProperty("arguments").GetString() ?? "{}"
                })
                .ToList()
        };

        if (routine.Steps.Any(x => string.IsNullOrWhiteSpace(x.ToolName)))
            return AiToolResult.Failure("La rutina contiene un paso sin herramienta.");

        var routines = _routines.Load().ToList();
        routines.Add(routine);
        _routines.Save(routines);

        return AiToolResult.Success($"Rutina «{name}» creada con {routine.Steps.Count} pasos.");
    }

    private AiToolResult ExecuteAiInspectRoutineTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        var routine = _routines.Find(name);
        if (routine is null)
            return AiToolResult.Failure($"No existe una rutina llamada «{name}».");

        var lines = new List<string>
        {
            $"Rutina: {routine.Name}",
            $"Descripción: {(string.IsNullOrWhiteSpace(routine.Description) ? "Sin descripción." : routine.Description)}",
            $"Pasos: {routine.Steps.Count}"
        };

        for (var i = 0; i < routine.Steps.Count; i++)
            lines.Add($"{i + 1}. {routine.Steps[i].ToolName} → {routine.Steps[i].ArgumentsJson}");

        return AiToolResult.Success(string.Join(Environment.NewLine, lines));
    }

    private AiToolResult ExecuteAiEditRoutineTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        var routine = _routines.Find(name);
        if (routine is null)
            return AiToolResult.Failure($"No existe una rutina llamada «{name}».");

        if (root.TryGetProperty("new_name", out var newNameElement))
        {
            var newName = newNameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return AiToolResult.Failure("El nuevo nombre no puede estar vacío.");

            if (!string.Equals(newName, routine.Name, StringComparison.OrdinalIgnoreCase) &&
                _routines.Find(newName) is not null)
                return AiToolResult.Failure($"Ya existe una rutina llamada «{newName}».");

            routine.Name = newName;
        }

        if (root.TryGetProperty("description", out var descriptionElement))
            routine.Description = descriptionElement.GetString()?.Trim() ?? "";

        if (root.TryGetProperty("steps", out var steps))
        {
            if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
                return AiToolResult.Failure("La rutina necesita al menos un paso.");

            var newSteps = new List<RoutineStep>();
            foreach (var step in steps.EnumerateArray())
            {
                var tool = step.TryGetProperty("tool", out var toolElement)
                    ? toolElement.GetString()?.Trim()
                    : null;
                var arguments = step.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement.GetString()
                    : "{}";

                if (string.IsNullOrWhiteSpace(tool))
                    return AiToolResult.Failure("La rutina contiene un paso sin herramienta.");

                try
                {
                    using var document = JsonDocument.Parse(arguments ?? "{}");
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return AiToolResult.Failure($"Los argumentos del paso «{tool}» no son un objeto JSON.");
                }
                catch (JsonException)
                {
                    return AiToolResult.Failure($"Los argumentos del paso «{tool}» no son JSON válido.");
                }

                newSteps.Add(new RoutineStep { ToolName = tool, ArgumentsJson = arguments ?? "{}" });
            }

            routine.Steps = newSteps;
        }

        return _routines.Update(routine)
            ? AiToolResult.Success($"Rutina «{routine.Name}» actualizada correctamente.")
            : AiToolResult.Failure("No pude guardar los cambios de la rutina.");
    }

    private AiToolResult ExecuteAiListRoutinesTool()
    {
        var routines = _routines.Load();
        if (routines.Count == 0)
            return AiToolResult.Success("No hay rutinas guardadas.");

        return AiToolResult.Success(string.Join(
            Environment.NewLine,
            routines.Select(x => $"• {x.Name}: {x.Description} ({x.Steps.Count} pasos)")));
    }

    private AiToolResult ExecuteAiDeleteRoutineTool(JsonElement root)
    {
        var name = GetToolString(root, "name").Trim();
        var routines = _routines.Load().ToList();
        var routine = routines.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (routine is null)
            return AiToolResult.Failure($"No existe una rutina llamada «{name}».");

        routines.Remove(routine);
        _routines.Save(routines);
        return AiToolResult.Success($"Rutina «{routine.Name}» eliminada.");
    }

    private async Task<AiToolResult> ExecuteAiRoutineToolAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var name = GetToolString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return AiToolResult.Failure("No se indicó el nombre de la rutina.");

        var routine = _routines.Find(name);
        if (routine is null)
            return AiToolResult.Failure($"No existe una rutina llamada «{name}».");

        if (routine.Steps.Count == 0)
            return AiToolResult.Failure($"La rutina «{routine.Name}» no tiene pasos.");

        if (routine.Steps.Any(x => string.Equals(x.ToolName, "run_routine", StringComparison.OrdinalIgnoreCase)))
            return AiToolResult.Failure("Una rutina no puede ejecutar otra rutina. Esto evita recursión y bucles.");

        _context.StartTask($"Ejecutar rutina «{routine.Name}»");
        _runningRoutine = true;

        try
        {
            for (var i = 0; i < routine.Steps.Count; i++)
        {
            var step = routine.Steps[i];
            var result = await ExecuteAiToolAsync(step.ToolName, step.ArgumentsJson, cancellationToken);

            if (!result.Succeeded)
            {
                _context.FailTask(result.Message);
                return AiToolResult.Failure($"La rutina «{routine.Name}» se detuvo en el paso {i + 1}: {result.Message}");
            }
            }

            _context.CompleteTask();
            return AiToolResult.Success($"Rutina «{routine.Name}» ejecutada correctamente.");
        }
        finally
        {
            _runningRoutine = false;
        }
    }

    private AiToolResult ExecuteAiSystemStateTool()
    {
        try
        {
            var foreground = GetForegroundWindow();
            var title = foreground == IntPtr.Zero ? string.Empty : GetWindowTitle(foreground);
            var processName = string.Empty;
            var processId = 0u;

            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out processId);

                if (processId != 0)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        processName = process.ProcessName;
                    }
                    catch
                    {
                    }
                }

                _context.SetActiveWindow(title, processName);
            }

            return AiToolResult.Success(_context.BuildSummary() +
                Environment.NewLine +
                $"PID activo: {processId}.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo obtener el estado de Windows");
            return AiToolResult.Failure("No pude consultar el estado actual de Windows.");
        }
    }

    private AiToolResult ExecuteAiListOpenWindowsTool()
    {
        try
        {
            var windows = new List<string>();

            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle))
                    return true;

                var title = GetWindowTitle(handle);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                GetWindowThreadProcessId(handle, out var processId);

                var processName = string.Empty;
                if (processId != 0)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        processName = process.ProcessName;
                    }
                    catch
                    {
                    }
                }

                windows.Add(processName.Length == 0
                    ? title
                    : $"{title} [{processName}]");

                return windows.Count < 50;
            }, IntPtr.Zero);

            if (windows.Count == 0)
                return AiToolResult.Success("No encontré ventanas visibles con título.");

            return AiToolResult.Success(
                $"Ventanas visibles ({windows.Count}):{Environment.NewLine}" +
                string.Join(Environment.NewLine, windows.Select((x, i) => $"{i + 1}. {x}")));
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo enumerar las ventanas abiertas");
            return AiToolResult.Failure("No pude consultar las ventanas abiertas.");
        }
    }

    private AiToolResult ExecuteAiSystemInfoTool()
    {
        try
        {
            var memory = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(systemDrive);

            var lines = new List<string>
            {
                $"Sistema: {Environment.OSVersion}",
                $"Equipo: {Environment.MachineName}",
                $"Procesadores lógicos: {Environment.ProcessorCount}",
                $"Arquitectura: {RuntimeInformation.OSArchitecture}"
            };

            if (GlobalMemoryStatusEx(ref memory))
            {
                lines.Add($"Memoria RAM: {FormatBytes(memory.TotalPhys)} total, {FormatBytes(memory.AvailPhys)} disponible");
                lines.Add($"Uso de RAM: {memory.MemoryLoad}%");
            }

            lines.Add($"Disco {drive.Name}: {FormatBytes(drive.AvailableFreeSpace)} libres de {FormatBytes(drive.TotalSize)}");

            return AiToolResult.Success(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo consultar la información del sistema");
            return AiToolResult.Failure("No pude consultar la información del equipo.");
        }
    }

    private AiToolResult ExecuteAiRunningAppsTool()
    {
        try
        {
            var apps = new List<string>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    var title = process.MainWindowTitle?.Trim();
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    apps.Add($"{title} [{process.ProcessName}] (PID {process.Id})");
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (apps.Count == 0)
                return AiToolResult.Success("No encontré aplicaciones con una ventana principal activa.");

            return AiToolResult.Success(
                $"Aplicaciones con ventana activa ({apps.Count}):{Environment.NewLine}" +
                string.Join(Environment.NewLine, apps
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .Select((x, i) => $"{i + 1}. {x}")));
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudieron enumerar las aplicaciones activas");
            return AiToolResult.Failure("No pude consultar las aplicaciones abiertas.");
        }
    }

    private AiToolResult ExecuteAiClipboardTool(JsonElement root)
    {
        var action = GetToolString(root, "action").Trim().ToLowerInvariant();

        if (action is "leer" or "read")
        {
            try
            {
                if (!Clipboard.ContainsText())
                    return AiToolResult.Success("El portapapeles no contiene texto.");

                var value = Clipboard.GetText();
                if (string.IsNullOrEmpty(value))
                    return AiToolResult.Success("El portapapeles contiene texto vacío.");

                return AiToolResult.Success($"Contenido del portapapeles:{Environment.NewLine}{value}");
            }
            catch (Exception ex)
            {
                App.LogException(ex, "No se pudo leer el portapapeles");
                return AiToolResult.Failure("No pude leer el portapapeles.");
            }
        }

        if (action is "escribir" or "write")
        {
            var text = GetToolString(root, "text");
            if (string.IsNullOrEmpty(text))
                return AiToolResult.Failure("No se indicó el texto que debe copiarse al portapapeles.");

            try
            {
                Clipboard.SetText(text);
                return AiToolResult.Success("Texto copiado al portapapeles.");
            }
            catch (Exception ex)
            {
                App.LogException(ex, "No se pudo escribir en el portapapeles");
                return AiToolResult.Failure("No pude escribir en el portapapeles.");
            }
        }

        return AiToolResult.Failure("La acción del portapapeles debe ser leer o escribir.");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        var units = new[] { "KB", "MB", "GB", "TB" };
        var value = (double)bytes / 1024;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private AiToolResult ExecuteAiPreferencesTool(JsonElement root)
    {
        var topic = GetToolString(root, "topic");
        if (string.IsNullOrWhiteSpace(topic))
            return AiToolResult.Failure("No se indicó el tema de la preferencia.");

        var tokens = Tokenize(topic);
        var matches = _memory.LoadLocalMemory()
            .Where(x => string.Equals(x.Category, "preference", StringComparison.OrdinalIgnoreCase))
            .Where(x => Tokenize((x.Subject ?? "") + " " + x.Text).Any(tokens.Contains))
            .OrderByDescending(x => x.UpdatedAt)
            .Take(8)
            .Select(x => $"[{x.Subject ?? "preferencia"}] {x.Text.Trim()}")
            .ToArray();

        return matches.Length == 0
            ? AiToolResult.Success($"No tengo preferencias guardadas relacionadas con «{topic}».")
            : AiToolResult.Success(string.Join(Environment.NewLine, matches));
    }

    private static HashSet<string> Tokenize(string value)
    {
        return new HashSet<string>(
            Regex.Split(value.ToLowerInvariant(), @"[^\\p{L}\\p{N}]+")
                .Where(x => x.Length >= 3),
            StringComparer.OrdinalIgnoreCase);
    }

    private AiToolResult ExecuteAiContextTool()
    {
        try
        {
            var foreground = GetForegroundWindow();
            var activeWindow = foreground == IntPtr.Zero
                ? null
                : GetWindowTitle(foreground);

            var lines = new List<string>();
            lines.Add(_context.BuildSummary());

            if (!string.IsNullOrWhiteSpace(activeWindow))
                lines.Add($"Ventana detectada ahora: {activeWindow}");

            if (!string.IsNullOrWhiteSpace(_context.LastFile) && File.Exists(_context.LastFile))
                lines.Add($"Último archivo: {_context.LastFile}");

            if (!string.IsNullOrWhiteSpace(_context.LastFolder) && Directory.Exists(_context.LastFolder))
                lines.Add($"Última carpeta: {_context.LastFolder}");

            if (!string.IsNullOrWhiteSpace(_context.CurrentPath) && Directory.Exists(_context.CurrentPath))
                lines.Add($"Ubicación actual: {_context.CurrentPath}");

            if (!string.IsNullOrWhiteSpace(_context.LastToolAction))
                lines.Add($"Última acción de EV: {_context.LastToolAction}");

            if (!string.IsNullOrWhiteSpace(_context.LastToolResult))
                lines.Add($"Resultado de la última acción: {_context.LastToolResult}");

            if (_context.LastResults.Count > 0)
            {
                lines.Add("Resultados recientes:");
                lines.AddRange(_context.LastResults.Take(10).Select((path, index) =>
                    $"{index + 1}. {path}"));
            }

            return lines.Count == 0
                ? AiToolResult.Success("No hay contexto adicional disponible en este momento.")
                : AiToolResult.Success(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo obtener el contexto actual de EV");
            return AiToolResult.Failure("No pude consultar el contexto actual de EV.");
        }
    }

    private static string GetToolString(System.Text.Json.JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static AiToolResult ExecuteAiApplicationTool(
        System.Text.Json.JsonElement root,
        ApplicationAction action)
    {
        if (!root.TryGetProperty("application", out var application) ||
            string.IsNullOrWhiteSpace(application.GetString()))
            return AiToolResult.Failure("No se indicó la aplicación.");

        var target = application.GetString()!;
        var result = ExecuteApplicationCommand(new ApplicationCommand(action, target));
        return result.Succeeded
            ? AiToolResult.Success(result.Response)
            : AiToolResult.Failure(result.Response);
    }

    private static AiToolResult ExecuteAiWindowTool(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement))
            return AiToolResult.Failure("No se indicó la acción de ventana.");

        var action = actionElement.GetString();
        if (string.IsNullOrWhiteSpace(action))
            return AiToolResult.Failure("No se indicó la acción de ventana.");

        var target = root.TryGetProperty("target", out var targetElement)
            ? targetElement.GetString() ?? string.Empty
            : string.Empty;

        var success = action switch
        {
            "switch" => TrySwitch(target),
            "minimize" => TryMinimize(target),
            "maximize" => TryMaximize(target),
            "restore" => TryRestore(target),
            "desktop" => ExecuteWindowCommand(new WindowCommand(WindowAction.Desktop)).Succeeded,
            "task_switcher" => ExecuteWindowCommand(new WindowCommand(WindowAction.TaskSwitcher)).Succeeded,
            _ => false
        };

        return success
            ? AiToolResult.Success("La acción de ventana se ejecutó correctamente.")
            : AiToolResult.Failure("No pude ejecutar esa acción de ventana.");
    }

    private static AiToolResult ExecuteAiSystemTool(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement))
            return AiToolResult.Failure("No se indicó la acción del sistema.");

        var action = actionElement.GetString();
        var systemAction = action switch
        {
            "settings" => SystemAction.Settings,
            "explorer" => SystemAction.Explorer,
            "calculator" => SystemAction.Calculator,
            "notepad" => SystemAction.Notepad,
            "lock" => SystemAction.Lock,
            "mute" => SystemAction.Mute,
            "volume_up" => SystemAction.VolumeUp,
            "volume_down" => SystemAction.VolumeDown,
            "play_pause" => SystemAction.PlayPause,
            "next_track" => SystemAction.NextTrack,
            "previous_track" => SystemAction.PreviousTrack,
            "stop_media" => SystemAction.StopMedia,
            _ => (SystemAction?)null
        };

        if (systemAction is null)
            return AiToolResult.Failure("No reconozco esa acción del sistema.");

        var result = ExecuteSystemCommand(new SystemCommand(systemAction.Value));
        return result.Succeeded
            ? AiToolResult.Success(result.Response)
            : AiToolResult.Failure(result.Response);
    }

    private AiToolResult ExecuteAiFindFileTool(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("file_name", out var fileNameElement) ||
            string.IsNullOrWhiteSpace(fileNameElement.GetString()))
            return AiToolResult.Failure("No se indicó el nombre del archivo.");

        var fileName = fileNameElement.GetString()!;
        var location = root.TryGetProperty("location", out var locationElement)
            ? locationElement.GetString()
            : null;
        var folderName = root.TryGetProperty("folder", out var folderElement)
            ? folderElement.GetString()
            : null;

        var searchRoot = ResolveAiLocation(location);
        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
            return AiToolResult.Failure("No encontré la ubicación donde buscar.");

        if (!string.IsNullOrWhiteSpace(folderName))
        {
            var folder = FindDirectory(searchRoot, folderName);
            if (folder is null)
                return AiToolResult.Failure($"No encontré la carpeta «{folderName}».");

            searchRoot = folder;
        }

        var files = FindFiles(searchRoot, fileName);
        if (files.Count == 0)
            return AiToolResult.Failure($"No encontré «{fileName}» en esa ubicación.");

        _context.SetResults(files);
        var listed = string.Join(Environment.NewLine,
            files.Take(10).Select((path, index) => $"{index + 1}. {path}"));

        return AiToolResult.Success($"Encontré {files.Count} archivo(s):{Environment.NewLine}{listed}");
    }

    private AiToolResult ExecuteAiOpenFileTool(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("file", out var fileElement) ||
            string.IsNullOrWhiteSpace(fileElement.GetString()))
            return AiToolResult.Failure("No se indicó qué archivo abrir.");

        var reference = fileElement.GetString()!;
        var path = ResolveAiFileSource(reference);

        if (path is null)
        {
            var location = root.TryGetProperty("location", out var locationElement)
                ? locationElement.GetString()
                : null;
            var searchRoot = ResolveAiLocation(location);

            if (!string.IsNullOrWhiteSpace(searchRoot) && Directory.Exists(searchRoot))
            {
                var files = FindFiles(searchRoot, reference);
                if (files.Count > 0)
                {
                    _context.SetResults(files);
                    path = files[0];
                }
            }
        }

        if (path is null || !File.Exists(path))
            return AiToolResult.Failure($"No encontré el archivo «{reference}».");

        try
        {
            StartShell(path);
            _context.SetOpenedFile(path);
            _context.SetResults([path]);
            return AiToolResult.Success($"Abrí «{Path.GetFileName(path)}».");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo abrir un archivo mediante la IA");
            return AiToolResult.Failure("No pude abrir ese archivo.");
        }
    }

    private AiToolResult ExecuteAiFileActionTool(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement) ||
            string.IsNullOrWhiteSpace(actionElement.GetString()))
            return AiToolResult.Failure("No se indicó qué operación hacer con el archivo.");

        if (!root.TryGetProperty("source", out var sourceElement) ||
            string.IsNullOrWhiteSpace(sourceElement.GetString()))
            return AiToolResult.Failure("No se indicó el archivo de origen.");

        var action = actionElement.GetString()!.ToLowerInvariant();
        var source = ResolveAiFileSource(sourceElement.GetString()!);

        if (source is null || !File.Exists(source))
            return AiToolResult.Failure("No encontré el archivo indicado.");

        FileActionType fileAction;
        string? destination = null;
        string? newName = null;

        switch (action)
        {
            case "copy":
                fileAction = FileActionType.Copy;
                destination = ResolveAiLocationProperty(root, "destination");
                if (destination is null || !Directory.Exists(destination))
                    return AiToolResult.Failure("No encontré la carpeta de destino.");
                break;

            case "move":
                fileAction = FileActionType.Move;
                destination = ResolveAiLocationProperty(root, "destination");
                if (destination is null || !Directory.Exists(destination))
                    return AiToolResult.Failure("No encontré la carpeta de destino.");
                break;

            case "rename":
                fileAction = FileActionType.Rename;
                if (!root.TryGetProperty("new_name", out var nameElement) ||
                    string.IsNullOrWhiteSpace(nameElement.GetString()))
                    return AiToolResult.Failure("No se indicó el nuevo nombre.");
                newName = Path.GetFileName(nameElement.GetString()!);
                if (string.IsNullOrWhiteSpace(newName))
                    return AiToolResult.Failure("El nuevo nombre no es válido.");
                break;

            case "delete":
                fileAction = FileActionType.Delete;
                break;

            default:
                return AiToolResult.Failure("No reconozco esa operación de archivo.");
        }

        var result = ExecuteFileAction(new FileActionCommand(
            fileAction, source, destination, newName));

        return result.Succeeded
            ? AiToolResult.Success(result.Response)
            : AiToolResult.Failure(result.Response);
    }

    private string? ResolveAiFileSource(string reference)
    {
        var normalized = Normalize(reference);

        if (normalized is "last" or "ultimo" or "último" or "ese" or "ese archivo")
            return _context.LastFile;

        if (normalized is "first" or "primero" or "primer archivo")
            return _context.LastResults.Count > 0 ? _context.LastResults[0] : _context.LastFile;

        if (normalized is "second" or "segundo" or "segundo archivo")
            return _context.LastResults.Count > 1 ? _context.LastResults[1] : null;

        if (Path.IsPathFullyQualified(reference) && File.Exists(reference))
            return reference;

        if (File.Exists(reference))
            return Path.GetFullPath(reference);

        return null;
    }

    private static string? ResolveAiLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var normalized = Normalize(location);

        var known = normalized switch
        {
            "escritorio" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "descargas" or "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "imagenes" or "fotos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };

        return known ?? (Directory.Exists(location) ? Path.GetFullPath(location) : null);
    }

    private static string? ResolveAiLocationProperty(System.Text.Json.JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            string.IsNullOrWhiteSpace(element.GetString()))
            return null;

        return ResolveAiLocation(element.GetString());
    }

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
            var item = CreateMemoryItem(memory);
            var result = await _memory.UpsertMemoryOnlineAsync(
                item,
                existing => MemoriesReferToSameSubject(existing, item),
                cancellationToken);

            return result.State switch
            {
                MemorySyncState.Success => CommandResult.Success(
                    "Listo, señor. Lo recordaré y actualizaré ese recuerdo si ya tenía información sobre el mismo tema."),
                MemorySyncState.Offline => CommandResult.Failure(
                    "No puedo guardar recuerdos nuevos o actualizar recuerdos existentes mientras estoy sin conexión."),
                MemorySyncState.NotConnected => CommandResult.Failure(
                    "Para guardar recuerdos entre computadoras, primero debes vincular tu cuenta de GitHub."),
                _ => CommandResult.Failure(result.Message)
            };
        }

        if (TryGetForgetMemoryRequest(command, out var memoryToForget))
        {
            var result = await _memory.ForgetMemoryOnlineAsync(memoryToForget, cancellationToken);

            if (result.Result.State == MemorySyncState.Success && result.Removed > 0)
                return CommandResult.Success("Listo, señor. Ya olvidé ese recuerdo y eliminé la copia sincronizada.");

            if (result.Result.State == MemorySyncState.Success)
                return CommandResult.Failure("No encontré un recuerdo que coincida con eso.");

            return CommandResult.Failure(result.Result.Message);
        }

        if (TryGetMemoryListRequest(command, out _))
        {
            var memories = new MemoryStore().Load()
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(10)
                .Select((x, i) => $"{i + 1}. {x.Text.Trim()}")
                .ToArray();

            return memories.Length == 0
                ? CommandResult.Success("Ahora mismo no tengo recuerdos permanentes guardados, señor.")
                : CommandResult.Success("Estos son mis recuerdos permanentes más recientes, señor:" +
                    Environment.NewLine + string.Join(Environment.NewLine, memories));
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

        var multiStepTask = LooksLikeMultiStepTask(command);
        if (multiStepTask)
            _context.StartTask(command);

        var conversation = await _conversation.RespondAsync(command, cancellationToken);

        if (multiStepTask)
        {
            if (conversation.Succeeded)
                _context.CompleteTask();
            else
                _context.FailTask(conversation.Text);
        }

        if (conversation.Succeeded)
            return CommandResult.Success(conversation.Text);

        return CommandResult.Failure(conversation.Text);
    }

    private static bool MemoriesReferToSameSubject(MemoryItem existing, MemoryItem incoming)
    {
        if (!string.Equals(existing.Category, incoming.Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(existing.Subject) &&
            !string.IsNullOrWhiteSpace(incoming.Subject))
        {
            return Normalize(existing.Subject) == Normalize(incoming.Subject);
        }

        var existingTokens = MemoryTokens(existing.Text);
        var incomingTokens = MemoryTokens(incoming.Text);

        if (existingTokens.Count == 0 || incomingTokens.Count == 0)
            return false;

        var common = existingTokens.Intersect(incomingTokens, StringComparer.OrdinalIgnoreCase).Count();
        return common >= 2;
    }

    private static HashSet<string> MemoryTokens(string value)
    {
        return new HashSet<string>(
            System.Text.RegularExpressions.Regex
                .Split(Normalize(value), @"[^a-z0-9áéíóúüñ]+")
                .Where(x => x.Length >= 4),
            StringComparer.OrdinalIgnoreCase);
    }

    private static MemoryItem CreateMemoryItem(string text)
    {
        var normalized = Normalize(text);
        var category = "general";

        if (ContainsAny(normalized, "prefiero", "prefiere", "me gusta mas", "me gusta más", "quiero que siempre"))
            category = "preference";
        else if (ContainsAny(normalized, "soy ", "tengo ", "estudio ", "trabajo ", "vivo ", "mi nombre"))
            category = "profile";
        else if (ContainsAny(normalized, "siempre ", "nunca ", "recuerda que"))
            category = "fact";

        return new MemoryItem
        {
            Text = text.Trim(),
            Category = category,
            Subject = ExtractMemorySubject(text, category)
        };
    }

    private static string? ExtractMemorySubject(string text, string category)
    {
        if (category != "preference")
            return null;

        var normalized = Normalize(text);
        foreach (var marker in new[] { "prefiero ", "prefiere ", "me gusta mas ", "me gusta más " })
        {
            var index = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var subject = text.Trim()[(index + marker.Length)..].Trim();
                return subject.Length > 0 ? subject : null;
            }
        }

        return null;
    }

    private static bool LooksLikeMultiStepTask(string text)
    {
        var normalized = Normalize(text);
        var connectors = new[]
        {
            " y ", " luego ", " después ", " despues ", " también ", " tambien ",
            " antes de ", " después de ", " despues de ", " primero ", " finalmente "
        };

        return connectors.Any(normalized.Contains);
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

    private static bool TryGetForgetMemoryRequest(string text, out string memory)
    {
        memory = string.Empty;
        var normalized = Normalize(text);

        var prefixes = new[]
        {
            "olvida que ",
            "olvida ",
            "olvidate de ",
            "olvídate de ",
            "borra de tu memoria ",
            "elimina de tu memoria "
        };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            memory = text.Trim()[prefix.Length..].Trim();
            return memory.Length > 0;
        }

        return false;
    }

    private static bool TryGetMemoryListRequest(string text, out string ignored)
    {
        ignored = string.Empty;
        var normalized = Normalize(text);

        return normalized is "que recuerdas" or "qué recuerdas" ||
               normalized.Contains("que recuerdas de mi", StringComparison.Ordinal) ||
               normalized.Contains("qué recuerdas de mi", StringComparison.Ordinal) ||
               normalized.Contains("muestrame mis recuerdos", StringComparison.Ordinal) ||
               normalized.Contains("muéstrame mis recuerdos", StringComparison.Ordinal);
    }

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

                case SystemAction.PlayPause:
                    SendVirtualKey(0xB3);
                    return CommandResult.Success("Reproducción pausada o reanudada, señor.");

                case SystemAction.NextTrack:
                    SendVirtualKey(0xB0);
                    return CommandResult.Success("Pasando a la siguiente canción, señor.");

                case SystemAction.PreviousTrack:
                    SendVirtualKey(0xB1);
                    return CommandResult.Success("Volviendo a la canción anterior, señor.");

                case SystemAction.StopMedia:
                    SendVirtualKey(0xB2);
                    return CommandResult.Success("Reproducción detenida, señor.");
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
        if (string.IsNullOrWhiteSpace(target))
            return GetForegroundWindow();

        var normalizedTarget = Normalize(target);
        var processTarget = GetProcessName(target);
        IntPtr bestHandle = IntPtr.Zero;
        var bestScore = 0;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            var title = GetWindowTitle(handle);
            if (title.Length == 0)
                return true;

            GetWindowThreadProcessId(handle, out var processId);
            var processName = string.Empty;

            if (processId != 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                }
                catch
                {
                }
            }

            var normalizedTitle = Normalize(title);
            var normalizedProcess = Normalize(processName);
            var score = 0;

            if (normalizedTitle.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                score += 100;

            if (normalizedTitle.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                score += 60;

            if (normalizedProcess.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                score += 80;

            if (!string.IsNullOrWhiteSpace(processTarget) &&
                normalizedProcess.Equals(Normalize(processTarget), StringComparison.OrdinalIgnoreCase))
                score += 100;

            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = handle;
            }

            return true;
        }, IntPtr.Zero);

        return bestHandle;
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
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

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
    private enum SystemAction { Settings, Explorer, Calculator, Notepad, Lock, Mute, VolumeUp, VolumeDown, PlayPause, NextTrack, PreviousTrack, StopMedia }
}

public sealed record CommandResult(bool Succeeded, string Response)
{
    public static CommandResult Success(string response) => new(true, response);
    public static CommandResult Failure(string response) => new(false, response);
}
