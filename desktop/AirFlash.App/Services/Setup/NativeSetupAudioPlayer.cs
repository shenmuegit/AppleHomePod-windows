using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace AirFlash.App.Services.Setup;

internal sealed class NativeSetupAudioPlayer : IAsyncDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly NativeSetupAudioLifetime _lifetime;
    private SetupConfirmationPlayback? _source;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _client;
    private WasapiOut? _player;
    private AudioSessionControl? _session;
    private EventHandler<StoppedEventArgs>? _playbackStopped;
    private float _originalGain;
    private bool _originalMute, _volumeCaptured;
    private int _disposed;
    internal Task Ready => _lifetime.Ready;
    internal TimeSpan? LastDispatchDelay => _source?.LastDispatchDelay;
    internal NativeSetupAudioPlayer(NativeBinding binding, Func<bool> isCurrent)
    {
        if (NativeBindingPolicy.PlaybackQualification(binding) is { } invalid) throw new InvalidDataException(invalid);
        ArgumentNullException.ThrowIfNull(isCurrent);
        _lifetime = new(error => _source?.Stop(error));
        var thread = new Thread(() => _lifetime.Run(() => RunCore(binding, isCurrent), Cleanup))
            { IsBackground = true, Name = "AppleHome setup confirmation" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    internal async Task PlayAsync(CancellationToken cancellation, bool silent = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await Ready.WaitAsync(cancellation).ConfigureAwait(false);
        await _source!.PlayAsync(cancellation, silent).ConfigureAwait(false);
    }
    private void RunCore(NativeBinding binding, Func<bool> isCurrent)
    {
        if (_stop.IsSet) { _lifetime.CancelReady(); return; }
        _enumerator = new MMDeviceEnumerator();
        _device = _enumerator.GetDevice(binding.Endpoint.EndpointId);
        if (_device.DataFlow != DataFlow.Render || _device.State != DeviceState.Active)
            throw new InvalidOperationException("The bound setup render endpoint is unavailable.");
        _client = _device.AudioClient;
        var format = _client.MixFormat;
        // WASAPI's shared float mix can differ from the verified device format's integer sample encoding.
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{format.SampleRate}/{format.Channels}/");
        if (!binding.Endpoint.MixFormat.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The bound setup render format changed before audio preparation.");
        _source = new SetupConfirmationPlayback(format.SampleRate, format.Channels);
        _player = new WasapiOut(_device, AudioClientShareMode.Shared, true, 50);
        _playbackStopped = (_, e) => {
            var error = e.Exception ?? new IOException("The prepared setup render source stopped.");
            _lifetime.ReportFailure(error);
            try { _stop.Set(); }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
        };
        _player.PlaybackStopped += _playbackStopped;
        _player.Init(new Provider(_source, format.SampleRate, format.Channels, isCurrent));
        var preparation = Stopwatch.GetTimestamp();
        _player.Play();
        for (var attempt = 0; _session is null; attempt++)
        {
            if (_stop.IsSet) { _lifetime.CancelReady(); return; }
            try { _session = NativeVolumeCalibration.FindSourceSession(_device, Environment.ProcessId); }
            catch (IOException) when (attempt < 40) { _stop.Wait(25); }
        }
        _originalGain = _session.SimpleAudioVolume.Volume; _originalMute = _session.SimpleAudioVolume.Mute;
        _volumeCaptured = true;
        _session.SimpleAudioVolume.Volume = 1; _session.SimpleAudioVolume.Mute = false;
        _lifetime.MarkReady();
        AppPaths.Log($"Setup confirmation render prepared_ms={Stopwatch.GetElapsedTime(preparation).TotalMilliseconds:F1}; idle_pcm=silence");
        _stop.Wait();
    }
    private void Cleanup()
    {
        var errors = new List<Exception>();
        void Release(Action release) { try { release(); } catch (Exception error) { errors.Add(error); } }
        if (_player is not null)
        {
            _player.PlaybackStopped -= _playbackStopped;
            Release(_player.Stop);
        }
        if (_session is not null)
        {
            if (_volumeCaptured) Release(() =>
            {
                if (_session.SimpleAudioVolume.Volume == 1 && !_session.SimpleAudioVolume.Mute)
                { _session.SimpleAudioVolume.Volume = _originalGain; _session.SimpleAudioVolume.Mute = _originalMute; }
            });
            Release(_session.Dispose);
        }
        if (_player is not null) Release(_player.Dispose);
        if (_source is not null) Release(_source.Dispose);
        if (_client is not null) Release(_client.Dispose);
        if (_device is not null) Release(_device.Dispose);
        if (_enumerator is not null) Release(_enumerator.Dispose);
        if (errors.Count > 0) throw new AggregateException("Setup audio resources did not release safely.", errors);
    }
    public async ValueTask DisposeAsync()
    {
        var dispose = Interlocked.Exchange(ref _disposed, 1) == 0;
        if (dispose) _stop.Set();
        try { await _lifetime.Stopped.ConfigureAwait(false); }
        finally { if (dispose) _stop.Dispose(); }
    }
    private sealed class Provider(SetupConfirmationPlayback source, int sampleRate, int channels, Func<bool> isCurrent) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                if (!isCurrent())
                {
                    source.RejectPlayback(new IOException("The monitored setup render endpoint changed or is awaiting validation."));
                    buffer.AsSpan(offset, count).Clear(); return count;
                }
            }
            catch (Exception error) { source.Stop(error); }
            source.Read(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count))); return count;
        }
    }
}
