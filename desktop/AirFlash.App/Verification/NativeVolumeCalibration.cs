using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using AirFlash.App.Services;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace AirFlash.App.Verification;

internal static class NativeVolumeCalibration
{
    internal static bool IsSourceCommand(string[] args) => args.Contains("--native-calibration-source");
    internal sealed record Proposal(PcmVolumeCalibration Calibration, IReadOnlyList<VolumeSample> Samples,
        VolumeSample SessionHalf, VolumeSample Mute, VolumeSample Zero, IReadOnlyList<long> CaptureSampleCounts, string CaptureFormat);

    internal static IDisposable AcquireMaintenance()
    {
        using var identity=WindowsIdentity.GetCurrent();
        var mutex=new Mutex(false,@"Local\"+AppPaths.Name+".Wpf."+identity.User!.Value+".Native",out var created);
        if (created) return mutex;
        mutex.Dispose();
        throw new InvalidOperationException("Stop the native output agent before endpoint maintenance or calibration.");
    }

    internal static async Task<NativeBinding> ReadBoundAsync(CancellationToken cancellation)
    {
        var binding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load()
            ?? throw new InvalidOperationException("Bind a verified virtual output before calibration.");
        var endpoints = await new WindowsNativeBinding().SnapshotAsync(cancellation).ConfigureAwait(false);
        var live = endpoints.SingleOrDefault(e => e.EndpointId == binding.Endpoint.EndpointId);
        if (live is null || !NativeBindingPolicy.CanClaim(live, new HashSet<string>())
            || live.AdapterInstanceId != binding.Endpoint.AdapterInstanceId || live.MixFormat != binding.Endpoint.MixFormat)
            throw new InvalidOperationException("The bound virtual output or its format changed. Revalidate it before calibration.");
        if (binding.Calibration is null && live.EndpointLabel == live.PinName && live.FriendlyName != binding.AssignedName)
            throw new InvalidOperationException("Complete the first-time Windows output naming before calibration.");
        if (!WindowsNativeBinding.RebootedSincePreinstall())
            throw new InvalidOperationException("Complete the independent driver's required reboot before capture or calibration.");
        return binding;
    }

    internal static async Task<Proposal> MeasureAsync(NativeBinding binding, CancellationToken cancellation)
    {
        var measured = await WindowsNativeBinding.OnMtaAsync(
            () => CaptureAsync(binding, cancellation).GetAwaiter().GetResult(), cancellation).ConfigureAwait(false);
        var reference = await ProbeReferenceAsync(binding.ReceiverId, cancellation).ConfigureAwait(false);
        var calibration = PcmVolumeMath.Classify(measured.Samples, binding.Endpoint.MixFormat, binding.Endpoint.DriverSha256, reference);
        return new(calibration, measured.Samples, measured.SessionHalf, measured.Mute, measured.Zero, measured.CaptureSampleCounts, measured.CaptureFormat);
    }

    internal static async Task<int> RunSourceAsync(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("The internal source takes no endpoint or gain overrides.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var binding = await ReadBoundAsync(timeout.Token).ConfigureAwait(false);
            return await WindowsNativeBinding.OnMtaAsync(() => {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(binding.Endpoint.EndpointId);
                using var client = device.AudioClient;
                var generator = new SignalGenerator(client.MixFormat.SampleRate, client.MixFormat.Channels)
                    { Gain = .04, Frequency = 997, Type = SignalGeneratorType.Sin };
                using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
                player.Init(generator);
                player.Play();
                Console.Out.NewLine="\n"; Console.Out.WriteLine("ready"); Console.Out.Flush();
                // The only input is a one-character stop; EOF and the 5-second cap also stop it.
                var input = new char[1];
                Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(5), timeout.Token), Console.In.ReadAsync(input,0,1)).GetAwaiter().GetResult();
                player.Stop();
                return 0;
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) { AppPaths.Log($"Calibration source failed: {error}"); return 1; }
    }

    private sealed record Captured(IReadOnlyList<VolumeSample> Samples, VolumeSample SessionHalf, VolumeSample Mute, VolumeSample Zero,
        IReadOnlyList<long> CaptureSampleCounts, string CaptureFormat);
    private static async Task<Captured> CaptureAsync(NativeBinding binding, CancellationToken cancellation)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(binding.Endpoint.EndpointId);
        using var capture = new WasapiLoopbackCapture(device);
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var volume = device.AudioEndpointVolume;
        var context = Guid.NewGuid(); volume.NotificationGuid = context;
        var originalDb = volume.MasterVolumeLevel; var originalMute = volume.Mute;
        var lastDb = originalDb; var lastMute = originalMute;
        var externalEndpointChange=0;
        AudioEndpointVolumeNotificationDelegate changed = data => {
            if (data.EventContext == context) return;
            Interlocked.Exchange(ref externalEndpointChange,1); interrupted.Cancel();
        };
        volume.OnVolumeNotification += changed;
        var token = interrupted.Token;
        var sync = new object(); PcmMeter? meter = null; Exception? captureError = null;
        var sampleCounts=new List<long>();
        var format = capture.WaveFormat;
        var ieee = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        capture.DataAvailable += (_, e) => {
            lock (sync) {
                if (meter is null) return;
                try { meter.Add(e.Buffer.AsSpan(0,e.BytesRecorded)); }
                catch (Exception error) { captureError = error; interrupted.Cancel(); }
            }
        };
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) { captureError = e.Exception; interrupted.Cancel(); } };
        Process? source = null; AudioSessionControl? sourceSession = null;
        float originalSession = 1, lastSession = 1; bool originalSessionMute = false;
        try
        {
            capture.StartRecording();
            var executable = Environment.ProcessPath ?? throw new IOException("The calibration executable is unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardInput=true, RedirectStandardOutput=true };
            start.ArgumentList.Add("--native-calibration-source");
            source = Process.Start(start) ?? throw new IOException("The isolated calibration source could not start.");
            var ready = new char[6];
            var readyCount = await source.StandardOutput.ReadBlockAsync(ready.AsMemory(),token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);
            if (readyCount != 6 || new string(ready) != "ready\n") throw new IOException("The isolated calibration source is not ready.");
            sourceSession = FindSourceSession(device, source.Id);
            originalSession = lastSession = sourceSession.SimpleAudioVolume.Volume;
            originalSessionMute = sourceSession.SimpleAudioVolume.Mute;
            sourceSession.SimpleAudioVolume.Mute = false;
            SetSession(1);
            SetEndpoint(volume.VolumeRange.MaxDecibels, false);
            await Task.Delay(150,token).ConfigureAwait(false);
            var samples = new List<VolumeSample>();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                SetEndpoint(volume.VolumeRange.MaxDecibels, false); samples.Add(await Measure().ConfigureAwait(false));
                SetEndpoint(volume.VolumeRange.MaxDecibels-12, false); samples.Add(await Measure().ConfigureAwait(false));
            }
            SetEndpoint(volume.VolumeRange.MaxDecibels,false); SetSession(.5f);
            var half = await Measure().ConfigureAwait(false);
            PcmVolumeMath.ValidateSessionGain(samples.Where(s => Math.Abs(s.EndpointDb-volume.VolumeRange.MaxDecibels)<.01).Average(s=>s.Rms),half.Rms);
            SetSession(1); SetEndpoint(volume.VolumeRange.MaxDecibels,true);
            var muted = await Measure(mayBeSilent:true).ConfigureAwait(false);
            SetEndpoint(volume.VolumeRange.MaxDecibels,false);
            volume.MasterVolumeLevelScalar=0; lastDb=volume.MasterVolumeLevel;
            var zero = await Measure(mayBeSilent:true).ConfigureAwait(false);
            if (captureError is not null) throw new IOException("PCM capture failed.",captureError);
            return new(samples,half,muted,zero,sampleCounts,format.ToString());

            void SetEndpoint(float db, bool mute)
            {
                token.ThrowIfCancellationRequested();
                if (Math.Abs(volume.MasterVolumeLevel-lastDb)>.01 || volume.Mute!=lastMute)
                    throw new OperationCanceledException("The user changed endpoint volume; calibration stopped.");
                volume.MasterVolumeLevel=db; lastDb=volume.MasterVolumeLevel;
                volume.Mute=mute; lastMute=volume.Mute;
            }
            void SetSession(float scalar)
            {
                token.ThrowIfCancellationRequested();
                if (Math.Abs(sourceSession.SimpleAudioVolume.Volume-lastSession)>.001 || sourceSession.SimpleAudioVolume.Mute)
                    throw new OperationCanceledException("The test application's volume changed; calibration stopped.");
                sourceSession.SimpleAudioVolume.Volume=scalar; lastSession=sourceSession.SimpleAudioVolume.Volume;
            }
            async Task<VolumeSample> Measure(bool mayBeSilent=false)
            {
                if (source.HasExited) throw new IOException("The bounded calibration source ended before measurements completed.");
                await Task.Delay(100,token).ConfigureAwait(false);
                lock(sync) meter=new PcmMeter(format.BitsPerSample,ieee);
                await Task.Delay(200,token).ConfigureAwait(false);
                (double Rms,double Peak,long Samples) measured;
                lock(sync) { measured=meter!.Snapshot(); meter=null; }
                sampleCounts.Add(measured.Samples);
                if (!mayBeSilent && measured.Samples < format.SampleRate*format.Channels/10)
                    throw new IOException("The calibration did not capture enough PCM samples.");
                return new(volume.MasterVolumeLevel,volume.MasterVolumeLevelScalar,volume.Mute,measured.Rms,measured.Peak);
            }
        }
        finally
        {
            lock(sync) meter=null;
            var errors=new List<Exception>();
            Cleanup(capture.StopRecording);
            Cleanup(()=>volume.OnVolumeNotification-=changed);
            Cleanup(()=>{
                if (Volatile.Read(ref externalEndpointChange)==0 && Math.Abs(volume.MasterVolumeLevel-lastDb)<.01 && volume.Mute==lastMute)
                { volume.MasterVolumeLevel=originalDb; volume.Mute=originalMute; }
            });
            Cleanup(()=>{
                if (sourceSession is not null && Math.Abs(sourceSession.SimpleAudioVolume.Volume-lastSession)<.001
                    && !sourceSession.SimpleAudioVolume.Mute)
                { sourceSession.SimpleAudioVolume.Volume=originalSession; sourceSession.SimpleAudioVolume.Mute=originalSessionMute; }
            });
            Cleanup(()=>sourceSession?.Dispose());
            Cleanup(()=>{
                if (source is not null)
                {
                    try { source.StandardInput.Close(); if (!source.WaitForExit(2000)) { source.Kill(); source.WaitForExit(); } }
                    finally { source.Dispose(); }
                }
            });
            if (errors.Count>0) throw new AggregateException("Calibration cleanup failed. Do not enable native output until recovery is checked.",errors);
            void Cleanup(Action action) { try { action(); } catch(Exception error) { errors.Add(error); } }
        }
    }

    private static AudioSessionControl FindSourceSession(MMDevice device, int sourcePid)
    {
        var manager=device.AudioSessionManager;
        manager.RefreshSessions();
        for (var index=0; index<manager.Sessions.Count; index++)
        {
            var session=manager.Sessions[index];
            if (session.GetProcessID==sourcePid) return session;
            session.Dispose();
        }
        throw new IOException("The owned test source's Windows audio session is unavailable.");
    }

    private static async Task<int> ProbeReferenceAsync(string receiverId, CancellationToken cancellation)
    {
        using var discovery=new WindowsDiscovery();
        var found=new TaskCompletionSource<Receiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Changed+=receivers => {
            var receiver=receivers.FirstOrDefault(r=>ReceiverIdentity.Normalize(r.Id)==ReceiverIdentity.Normalize(receiverId));
            if (receiver is not null) found.TrySetResult(receiver);
        };
        discovery.Failed+=error=>found.TrySetException(new IOException(error));
        discovery.Start();
        var selected=await found.Task.WaitAsync(TimeSpan.FromSeconds(8),cancellation).ConfigureAwait(false);
        if (!selected.PrefersNtpAlac) throw new InvalidOperationException("This receiver profile has not been qualified for native output.");
        var addresses=await Dns.GetHostAddressesAsync(selected.Address,cancellation).ConfigureAwait(false);
        var address=addresses.FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork)
            ?? throw new IOException("The bound receiver has no current IPv4 address.");
        var wav=Path.Combine(Path.GetTempPath(),"AppleHome-calibration-"+Guid.NewGuid().ToString("N")+".wav");
        try
        {
            var signal=new SignalGenerator(44100,2) { Gain=.04,Frequency=997,Type=SignalGeneratorType.Sin };
            WaveFileWriter.CreateWaveFile16(wav,signal.Take(TimeSpan.FromSeconds(5)));
            await using var engine=new ProcessEngineConnection(AppPaths.ExtractEngine(),AppPaths.Log);
            var session=Guid.NewGuid().ToString("N"); int? reference=null;
            await engine.SendAsync(session,"probe",new { peers=new[]{new { host=address.ToString(),port=selected.Port,codecs=new[]{1} }},
                source="wav",wav_path=wav,duration_ms=5000,latency_ms=2000,gain=.1,sample_rate=44100,timing="ntp" },cancellation).ConfigureAwait(false);
            while (true)
            {
                var item=await engine.ReadAsync(cancellation).ConfigureAwait(false) ?? throw new IOException("The reference probe exited before completion.");
                if (item.Number("version")!=1 || item.Text("session_id")!=session) continue;
                if (item.Text("event")=="error") throw new IOException(item.Text("message","Reference probe failed."));
                if (item.Text("event")=="device_volume" && item.Boolean("available")==true && item.Integer("volume") is >=0 and <=100)
                    reference=(int)item.Integer("volume")!.Value;
                if (item.Text("event")=="stopped") break;
            }
            if (reference is not (>=1 and <=100)) throw new InvalidOperationException("The receiver has no confirmed nonzero volume. Set an audible reference explicitly before retrying.");
            return reference.Value;
        }
        finally { if (File.Exists(wav)) File.Delete(wav); }
    }
}
