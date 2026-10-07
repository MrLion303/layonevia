using System.Text;
using System.Text.Json;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using EV.Services;
using NAudio.Wave;
using Vosk;

namespace EV.Services;

public sealed class VoiceRecognitionService : IDisposable
{
    private const int SampleRate = 16000;
    private const string ModelFolderName = "vosk-model-small-es-0.42";

    private readonly AudioSettingsStore _settingsStore = new();

    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _capture;
    private bool _disposed;
    private bool _listening;
    private bool _wakeArmed;
    private DateTime _wakeArmedUntil;
    private DateTime _lastCommandAt = DateTime.MinValue;
    private readonly object _sync = new();

    public event EventHandler<VoiceCommandEventArgs>? CommandRecognized;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? AudioLevelChanged;

    public bool IsListening => _listening;

    public void Start()
    {
        if (_disposed || _listening)
            return;

        try
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "models", ModelFolderName);

            if (!Directory.Exists(modelPath))
            {
                StatusChanged?.Invoke(this,
                    "Falta el modelo de reconocimiento de voz en español.");
                return;
            }

            _model = new Model(modelPath);
            Vosk.Vosk.SetLogLevel(-1);

            _recognizer = new VoskRecognizer(_model, SampleRate);
            _recognizer.SetMaxAlternatives(0);
            _recognizer.SetWords(false);

            var deviceNumber = FindWaveInDeviceNumber();
            if (deviceNumber < 0)
            {
                StatusChanged?.Invoke(this, "No hay un micrófono disponible.");
                StopInternal();
                return;
            }

            _capture = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 100
            };

            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
            _capture.StartRecording();

            _listening = true;
            _wakeArmed = false;
            _wakeArmedUntil = DateTime.MinValue;

            var activeDevice = WaveInEvent.GetCapabilities(deviceNumber).ProductName;
            StatusChanged?.Invoke(
                this,
                $"Escuchando «Oye ibi» · {activeDevice}");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo iniciar el reconocimiento de voz local");
            StopInternal();

            StatusChanged?.Invoke(
                this,
                "No se pudo iniciar el reconocimiento de voz local.");
        }
    }

    public void Stop()
    {
        StopInternal();
        StatusChanged?.Invoke(this, "Reconocimiento de voz detenido.");
    }

    public void Restart()
    {
        if (_disposed)
            return;

        StopInternal();
        Start();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            VoskRecognizer? recognizer;

            lock (_sync)
                recognizer = _recognizer;

            if (recognizer is null)
                return;

            AudioLevelChanged?.Invoke(this, CalculateAudioLevel(e.Buffer, e.BytesRecorded));

            var isFinal = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded);

            if (!isFinal)
            {
                var partial = ExtractPartialText(recognizer.PartialResult());
                if (!string.IsNullOrWhiteSpace(partial))
                    ProcessRecognizedText(partial, true);

                return;
            }

            var json = recognizer.Result();
            var text = ExtractText(json);

            if (string.IsNullOrWhiteSpace(text))
                return;

            ProcessRecognizedText(text, false);
        }
        catch (Exception ex)
        {
            App.LogException(ex, "Error procesando el audio del reconocimiento de voz");
        }
    }

    private void ProcessRecognizedText(string text, bool partial)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var wakeIndex = FindWakePhrase(tokens);
        if (wakeIndex >= 0)
        {
            _wakeArmed = true;
            _wakeArmedUntil = DateTime.UtcNow.AddSeconds(8);

            var command = wakeIndex + 2 < tokens.Length
                ? string.Join(' ', tokens[(wakeIndex + 2)..])
                : string.Empty;

            if (partial)
                return;

            if (DateTime.UtcNow - _lastCommandAt < TimeSpan.FromMilliseconds(900))
                return;

            CommandRecognized?.Invoke(
                this,
                new VoiceCommandEventArgs(text, command, 1.0f));

            _lastCommandAt = DateTime.UtcNow;
            _wakeArmed = false;
            return;
        }

        if (!partial &&
            _wakeArmed &&
            DateTime.UtcNow <= _wakeArmedUntil)
        {
            _wakeArmed = false;

            if (DateTime.UtcNow - _lastCommandAt < TimeSpan.FromMilliseconds(900))
                return;

            CommandRecognized?.Invoke(
                this,
                new VoiceCommandEventArgs(text, text, 1.0f));

            _lastCommandAt = DateTime.UtcNow;
        }
    }

    private static int FindWakePhrase(string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!IsCloseToOye(tokens[i]))
                continue;

            for (var j = i + 1; j < Math.Min(tokens.Length, i + 4); j++)
            {
                if (tokens[j] is "y" or "e")
                    continue;

                if (IsCloseToIbi(tokens[j]))
                    return i;
            }
        }

        return -1;
    }

    private static bool IsCloseToOye(string token)
    {
        return token is "oye" or "oi" or "oy" or "hoy" or "oie" ||
               LevenshteinDistance(token, "oye") <= 1;
    }

    private static bool IsCloseToIbi(string token)
    {
        return token is "ibi" or "ivi" or "ybi" or "yvi" or "vibi" ||
               LevenshteinDistance(token, "ibi") <= 1;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private int FindWaveInDeviceNumber()
    {
        try
        {
            var settings = _settingsStore.Load();
            var preferredId = settings.PreferredInputId;

            using var devices = new AudioDeviceManager().TryGetInput(preferredId);

            if (devices is not null)
            {
                var preferredName = devices.FriendlyName;
                var number = FindWaveInDeviceByName(preferredName);

                if (number >= 0)
                    return number;
            }

            var defaultName = GetDefaultInputName();

            var defaultNumber = FindWaveInDeviceByName(defaultName);
            if (defaultNumber >= 0)
                return defaultNumber;

            defaultNumber = FindWaveInDeviceByName(settings.PreferredInputName);
            if (defaultNumber >= 0)
                return defaultNumber;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo localizar el micrófono preferido");
        }

        return WaveInEvent.DeviceCount > 0 ? 0 : -1;
    }

    private static string? GetDefaultInputName()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.Role.Multimedia);

            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    private static int FindWaveInDeviceByName(string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return -1;

        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try
            {
                var capabilities = WaveInEvent.GetCapabilities(i);

                var deviceName = Normalize(capabilities.ProductName);
                var requestedName = Normalize(targetName);

                if (string.Equals(deviceName, requestedName, StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains(requestedName, StringComparison.OrdinalIgnoreCase) ||
                    requestedName.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            catch
            {
            }
        }

        return -1;
    }

    private static double CalculateAudioLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2)
            return 0;

        double sum = 0;
        var samples = bytesRecorded / 2;

        for (var i = 0; i < bytesRecorded - 1; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var normalized = sample / 32768.0;
            sum += normalized * normalized;
        }

        var rms = Math.Sqrt(sum / samples);
        return Math.Clamp(rms * 5.0, 0, 1);
    }

    private static string ExtractText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("text", out var text))
                return text.GetString()?.Trim() ?? string.Empty;
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractPartialText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("partial", out var text))
                return text.GetString()?.Trim() ?? string.Empty;
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string Normalize(string value)
    {
        var normalized = value
            .Normalize(NormalizationForm.FormD)
            .Where(c =>
                CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray();

        var withoutAccents = new string(normalized).ToLowerInvariant();

        return Regex.Replace(
            withoutAccents,
            @"[^\p{L}\p{Nd}]+",
            " ").Trim();
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || e.Exception is null)
            return;

        App.LogException(
            e.Exception,
            "El micrófono detuvo el reconocimiento de voz");
    }

    private void StopInternal()
    {
        _listening = false;

        lock (_sync)
        {
            if (_capture is not null)
            {
                try
                {
                    _capture.DataAvailable -= Capture_DataAvailable;
                    _capture.RecordingStopped -= Capture_RecordingStopped;
                    _capture.StopRecording();
                }
                catch
                {
                }

                _capture.Dispose();
                _capture = null;
            }

            _recognizer?.Dispose();
            _recognizer = null;

            _model?.Dispose();
            _model = null;
        }
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
