using System.Speech.Synthesis;
using EV.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EV.Services;

public sealed class VoiceOutputService : IDisposable
{
    private readonly AudioDeviceManager _devices = new();
    private readonly AudioSettingsStore _settingsStore = new();
    private readonly SemaphoreSlim _speechLock = new(1, 1);
    private bool _disposed;

    public async Task<string?> SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return null;

        await _speechLock.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Load();
            using var synthesizer = new SpeechSynthesizer();
            using var audio = new MemoryStream();

            synthesizer.SetOutputToWaveStream(audio);
            synthesizer.Speak(text);
            audio.Position = 0;

            var preferredId = settings.PreferredOutputId;
            using var device = _devices.TryGetOutput(preferredId);
            if (device is null)
                return null;

            try
            {
                await PlayAsync(device, audio, cancellationToken);
                return device.FriendlyName;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                audio.Position = 0;
                using var fallback = _devices.TryGetOutput(null);
                if (fallback is null || string.Equals(fallback.ID, device.ID, StringComparison.OrdinalIgnoreCase))
                    throw;

                await PlayAsync(fallback, audio, cancellationToken);
                return fallback.FriendlyName;
            }
        }
        finally
        {
            _speechLock.Release();
        }
    }

    private static Task PlayAsync(MMDevice device, Stream audio, CancellationToken cancellationToken)
    {
        var reader = new WaveFileReader(audio);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.Init(reader);

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Stopped(object? sender, StoppedEventArgs e)
        {
            output.PlaybackStopped -= Stopped;
            output.Dispose();
            reader.Dispose();

            if (e.Exception is not null)
                completion.TrySetException(e.Exception);
            else
                completion.TrySetResult(null);
        }

        output.PlaybackStopped += Stopped;
        output.Play();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                try { output.Stop(); }
                catch { }
            });
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _speechLock.Dispose();
    }
}
