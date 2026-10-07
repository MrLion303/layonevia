using System.IO;
using System.Speech.Synthesis;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EV.Services;

public sealed class VoiceOutputService : IDisposable
{
    public static IReadOnlyList<string> GetAvailableVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private readonly AudioDeviceManager _devices = new();
    private readonly AudioSettingsStore _settingsStore = new();
    private readonly SemaphoreSlim _speechLock = new(1, 1);
    private bool _disposed;

    public event EventHandler<double>? AudioLevelChanged;

    public async Task<string?> SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return null;
        await _speechLock.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Load();
            using var synthesizer = new SpeechSynthesizer();
            using var audio = new MemoryStream();
            var voices = synthesizer.GetInstalledVoices().Where(v => v.Enabled).ToArray();
            var preferredVoice = voices.FirstOrDefault(v => !string.IsNullOrWhiteSpace(settings.PreferredVoiceName) &&
                string.Equals(v.VoiceInfo.Name, settings.PreferredVoiceName, StringComparison.OrdinalIgnoreCase));
            if (preferredVoice is not null) synthesizer.SelectVoice(preferredVoice.VoiceInfo.Name);
            synthesizer.Rate = Math.Clamp(settings.VoiceRate, -10, 10);
            synthesizer.Volume = Math.Clamp(settings.VoiceVolume, 0, 100);
            synthesizer.SetOutputToWaveStream(audio);
            synthesizer.Speak(text);
            audio.Position = 0;

            using var device = _devices.TryGetOutput(settings.PreferredOutputId);
            if (device is null) return null;
            try
            {
                await PlayAsync(device, audio, cancellationToken);
                return device.FriendlyName;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                audio.Position = 0;
                using var fallback = _devices.TryGetOutput(null);
                if (fallback is null || string.Equals(fallback.ID, device.ID, StringComparison.OrdinalIgnoreCase)) throw;
                await PlayAsync(fallback, audio, cancellationToken);
                return fallback.FriendlyName;
            }
        }
        finally
        {
            SetAudioLevel(0);
            _speechLock.Release();
        }
    }

    private async Task PlayAsync(MMDevice device, Stream audio, CancellationToken cancellationToken)
    {
        audio.Position = 0;
        var levels = ReadAudioLevels(audio, out var frameDurationMs);
        audio.Position = 0;

        using var reader = new WaveFileReader(audio);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.Init(reader);

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Stopped(object? sender, StoppedEventArgs e)
        {
            output.PlaybackStopped -= Stopped;
            playbackCts.Cancel();
            if (e.Exception is not null) completion.TrySetException(e.Exception);
            else completion.TrySetResult(null);
        }

        output.PlaybackStopped += Stopped;
        output.Play();

        using var registration = cancellationToken.Register(() =>
        {
            try { output.Stop(); } catch { }
        });

        var levelTask = EmitAudioLevelsAsync(levels, frameDurationMs, playbackCts.Token);
        try
        {
            await completion.Task;
            try { await levelTask; } catch (OperationCanceledException) when (playbackCts.IsCancellationRequested) { }
        }
        finally
        {
            SetAudioLevel(0);
        }
    }

    private async Task EmitAudioLevelsAsync(IReadOnlyList<double> levels, int frameDurationMs, CancellationToken cancellationToken)
    {
        foreach (var level in levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetAudioLevel(level);
            await Task.Delay(frameDurationMs, cancellationToken);
        }
    }

    private static IReadOnlyList<double> ReadAudioLevels(Stream audio, out int frameDurationMs)
    {
        audio.Position = 0;
        using var reader = new WaveFileReader(audio);
        var format = reader.WaveFormat;
        const int targetMilliseconds = 50;
        var bytesPerFrame = Math.Max(format.AverageBytesPerSecond * targetMilliseconds / 1000, format.BlockAlign);
        bytesPerFrame -= bytesPerFrame % format.BlockAlign;
        frameDurationMs = Math.Max(20, (int)Math.Round(bytesPerFrame * 1000.0 / format.AverageBytesPerSecond));

        var buffer = new byte[bytesPerFrame];
        var levels = new List<double>();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            double sum = 0;
            var samples = 0;
            if (format.BitsPerSample == 16)
            {
                for (var i = 0; i + 1 < read; i += 2)
                {
                    var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                    sum += sample * sample;
                    samples++;
                }
            }
            else if (format.BitsPerSample == 8)
            {
                for (var i = 0; i < read; i++)
                {
                    var sample = (buffer[i] - 128) / 128.0;
                    sum += sample * sample;
                    samples++;
                }
            }
            levels.Add(samples == 0 ? 0 : Math.Clamp(Math.Sqrt(sum / samples) * 2.8, 0, 1));
        }
        return levels;
    }

    private void SetAudioLevel(double level)
    {
        try { AudioLevelChanged?.Invoke(this, Math.Clamp(level, 0, 1)); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetAudioLevel(0);
        _speechLock.Dispose();
    }
}