using System.Globalization;
using System.Speech.Recognition;
using System.Text;

namespace EV.Services;

public sealed class VoiceRecognitionService : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private bool _disposed;
    private bool _listening;

    public event EventHandler<VoiceCommandEventArgs>? CommandRecognized;
    public event EventHandler<string>? StatusChanged;

    public bool IsListening => _listening;

    public void Start()
    {
        if (_disposed || _listening)
            return;

        try
        {
            var culture = FindSpanishCulture();

            if (culture is null)
            {
                StatusChanged?.Invoke(this,
                    "No hay un reconocedor de voz en español instalado en Windows.");
                return;
            }

            _engine = new SpeechRecognitionEngine(culture);
            _engine.LoadGrammar(new DictationGrammar());
            _engine.SpeechRecognized += Engine_SpeechRecognized;
            _engine.RecognizeCompleted += Engine_RecognizeCompleted;
            _engine.SetInputToDefaultAudioDevice();
            _engine.RecognizeAsync(RecognizeMode.Multiple);

            _listening = true;
            StatusChanged?.Invoke(this, $"Escuchando «Oye ibi» · {culture.Name}");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo iniciar el reconocimiento de voz");
            StopInternal();
            StatusChanged?.Invoke(this,
                "El reconocimiento de voz no está disponible.");
        }
    }

    public void Stop()
    {
        StopInternal();
        StatusChanged?.Invoke(this, "Reconocimiento de voz detenido.");
    }

    private void Engine_SpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence < 0.55f)
            return;

        var text = e.Result.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var normalized = RemoveAccents(text).ToLowerInvariant();
        const string wakeWord = "oye ibi";

        var index = normalized.IndexOf(wakeWord, StringComparison.Ordinal);
        if (index < 0)
            return;

        var commandStart = index + wakeWord.Length;
        var command = text[commandStart..].Trim(' ', ',', '.', ';', ':', '¡', '!', '?', '¿');

        CommandRecognized?.Invoke(
            this,
            new VoiceCommandEventArgs(text, command, e.Result.Confidence));
    }

    private void Engine_RecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.Error is not null)
        {
            App.LogException(e.Error, "El reconocimiento de voz terminó con un error");
            _listening = false;
            StatusChanged?.Invoke(this,
                "El reconocimiento de voz se detuvo por un error.");
        }
    }

    private static CultureInfo? FindSpanishCulture()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();

        var preferred = installed.FirstOrDefault(x =>
            string.Equals(x.Culture.Name, "es-MX", StringComparison.OrdinalIgnoreCase));

        if (preferred is not null)
            return preferred.Culture;

        var spanish = installed.FirstOrDefault(x =>
            x.Culture.Name.StartsWith("es-", StringComparison.OrdinalIgnoreCase));

        return spanish?.Culture;
    }

    private static string RemoveAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray();

        return new string(chars).Normalize(NormalizationForm.FormC);
    }

    private void StopInternal()
    {
        _listening = false;

        if (_engine is null)
            return;

        try
        {
            _engine.RecognizeAsyncCancel();
            _engine.RecognizeAsyncStop();
        }
        catch
        {
        }

        _engine.SpeechRecognized -= Engine_SpeechRecognized;
        _engine.RecognizeCompleted -= Engine_RecognizeCompleted;
        _engine.Dispose();
        _engine = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopInternal();
    }
}

public sealed class VoiceCommandEventArgs : EventArgs
{
    public string HeardText { get; }
    public string Command { get; }
    public float Confidence { get; }

    public VoiceCommandEventArgs(string heardText, string command, float confidence)
    {
        HeardText = heardText;
        Command = command;
        Confidence = confidence;
    }
}
