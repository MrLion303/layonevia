using System.IO;
namespace EV.Services;

public sealed class ConversationContext
{
    private readonly List<string> _lastResults = [];
    private readonly List<ExecutedToolStep> _recentToolSequence = [];

    public string? CurrentPath { get; private set; }
    public string? LastFile { get; private set; }
    public string? LastFolder { get; private set; }
    public PendingFileAction? PendingAction { get; private set; }
    public FileActionRecord? LastAction { get; private set; }
    public TaskState? CurrentTask { get; private set; }
    public IReadOnlyList<ExecutedToolStep> RecentToolSequence => _recentToolSequence;
    public DateTimeOffset LastContextUpdate { get; private set; } = DateTimeOffset.UtcNow;

    public string BuildSummary()
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(ActiveWindowTitle))
            lines.Add($"Ventana activa: {ActiveWindowTitle}");

        if (!string.IsNullOrWhiteSpace(ActiveProcessName))
            lines.Add($"Proceso activo: {ActiveProcessName}");

        if (!string.IsNullOrWhiteSpace(ActiveApplication))
            lines.Add($"Aplicación recordada: {ActiveApplication}");

        if (!string.IsNullOrWhiteSpace(CurrentPath))
            lines.Add($"Ruta actual: {CurrentPath}");

        if (!string.IsNullOrWhiteSpace(LastFile))
            lines.Add($"Último archivo: {LastFile}");

        if (!string.IsNullOrWhiteSpace(LastFolder))
            lines.Add($"Última carpeta: {LastFolder}");

        if (CurrentTask is not null)
        {
            var step = CurrentTask.TotalSteps > 0
                ? $"paso {CurrentTask.StepNumber}/{CurrentTask.TotalSteps}"
                : "sin pasos definidos";
            lines.Add($"Tarea: {CurrentTask.Status}, {step}, objetivo: {CurrentTask.Goal}");

            if (!string.IsNullOrWhiteSpace(CurrentTask.CurrentStep))
                lines.Add($"Paso actual: {CurrentTask.CurrentStep}");
        }

        if (!string.IsNullOrWhiteSpace(LastToolAction))
            lines.Add($"Última acción: {LastToolAction}");

        if (_recentToolSequence.Count > 0)
        {
            lines.Add("Secuencia reciente:");
            foreach (var item in _recentToolSequence.TakeLast(8))
                lines.Add($"- {item.ToolName}: {item.Action}");
        }

        return lines.Count == 0
            ? "No hay contexto de Windows o acciones recientes disponibles."
            : string.Join(Environment.NewLine, lines);
    }

    public void StartTask(string goal)
    {
        CurrentTask = new TaskState(goal);
    }

    public void SetTaskStep(string step, int stepNumber, int totalSteps)
    {
        if (CurrentTask is null)
            return;

        CurrentTask.CurrentStep = step;
        CurrentTask.StepNumber = stepNumber;
        CurrentTask.TotalSteps = totalSteps;
        CurrentTask.Status = TaskStatus.Running;
    }

    public void CompleteTask()
    {
        if (CurrentTask is null)
            return;

        CurrentTask.Status = TaskStatus.Completed;
        CurrentTask.CurrentStep = null;
    }

    public void FailTask(string reason)
    {
        if (CurrentTask is null)
            return;

        CurrentTask.Status = TaskStatus.Failed;
        CurrentTask.LastError = reason;
    }

    public string? LastToolAction { get; private set; }
    public string? LastToolResult { get; private set; }
    public string? ActiveApplication { get; private set; }
    public string? ActiveWindowTitle { get; private set; }
    public string? ActiveProcessName { get; private set; }

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

    public void SetActiveWindow(string title, string? processName = null)
    {
        ActiveWindowTitle = string.IsNullOrWhiteSpace(title) ? null : title;
        ActiveProcessName = string.IsNullOrWhiteSpace(processName) ? null : processName;
        LastContextUpdate = DateTimeOffset.UtcNow;
    }

    public void SetActiveApplication(string application)
    {
        ActiveApplication = string.IsNullOrWhiteSpace(application) ? null : application;
        LastContextUpdate = DateTimeOffset.UtcNow;
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

    public void SetLastAction(FileActionType action, string sourcePath, string? destinationPath = null, string? newName = null)
    {
        LastAction = new FileActionRecord(action, sourcePath, destinationPath, newName);
    }

    public FileActionRecord? GetRepeatableAction()
    {
        if (LastAction is null || LastAction.Action != FileActionType.Copy || !File.Exists(LastAction.SourcePath))
            return null;

        return LastAction;
    }

    public void SetPendingAction(PendingFileAction action) => PendingAction = action;

    public void RecordToolAction(string action, string result)
    {
        LastToolAction = action;
        LastToolResult = result;
        if (CurrentTask is not null && !string.IsNullOrWhiteSpace(result))
            CurrentTask.LastResult = result;
    }

    public void RecordExecutedTool(string toolName, string argumentsJson, string action, string result)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(argumentsJson))
            return;

        if (_recentToolSequence.Count == 0 ||
            (DateTimeOffset.UtcNow - _recentToolSequence[^1].ExecutedAt).TotalMinutes > 5)
        {
            _recentToolSequence.Clear();
        }

        _recentToolSequence.Add(new ExecutedToolStep(
            toolName,
            argumentsJson,
            action,
            result,
            DateTimeOffset.UtcNow));

        while (_recentToolSequence.Count > 30)
            _recentToolSequence.RemoveAt(0);
    }

    public void ClearRecentToolSequence() => _recentToolSequence.Clear();

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
        LastAction = null;
        LastToolAction = null;
        CurrentTask = null;
        LastToolResult = null;
        ActiveApplication = null;
        ActiveWindowTitle = null;
        ActiveProcessName = null;
        _lastResults.Clear();
        _recentToolSequence.Clear();
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

public sealed record FileActionRecord(FileActionType Action, string SourcePath, string? DestinationPath, string? NewName);

public sealed class TaskState
{
    public TaskState(string goal) => Goal = goal;

    public string Goal { get; }
    public string? CurrentStep { get; set; }
    public int StepNumber { get; set; }
    public int TotalSteps { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Running;
    public string? LastResult { get; set; }
    public string? LastError { get; set; }
}

public enum TaskStatus
{
    Running,
    Completed,
    Failed
}


public sealed record ExecutedToolStep(
    string ToolName,
    string ArgumentsJson,
    string Action,
    string Result,
    DateTimeOffset ExecutedAt);
