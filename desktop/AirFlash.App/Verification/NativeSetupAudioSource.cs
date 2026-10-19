using AirFlash.App.Services;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace AirFlash.App.Verification;

internal static class NativeSetupAudioSource
{
    internal const string SourceFlag = "--native-setup-confirmation-source";
    internal static bool IsSourceCommand(string[] args) => args.Contains(SourceFlag, StringComparer.Ordinal);

    internal static async Task<int> RunSourceAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.Length != 1 || args[0] != SourceFlag)
                throw new ArgumentException("The internal setup source takes only its command flag, with no endpoint or gain overrides.");
            var binding = await NativeVolumeCalibration.ReadBoundAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return await WindowsNativeBinding.OnMtaAsync(() => {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(binding.Endpoint.EndpointId);
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                    throw new InvalidOperationException("The bound setup render endpoint is unavailable.");
                using var client = device.AudioClient;
                var format = client.MixFormat;
                var pcm = SetupConfirmationPcm.Create(format.SampleRate, format.Channels);
                var bytes = new byte[pcm.Length * sizeof(float)];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                var source = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
                    { BufferLength = bytes.Length, ReadFully = true };
                using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, e) => {
                    if (e.Exception is not null) stopped.TrySetException(e.Exception);
                    else stopped.TrySetResult();
                };
                player.Init(source);
                AudioSessionControl? session = null;
                float originalGain = 1; bool originalMute = false;
                try
                {
                    player.Play();
                    for (var attempt = 0; session is null; attempt++)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        try { session = NativeVolumeCalibration.FindSourceSession(device, Environment.ProcessId); }
                        catch (IOException) when (attempt < 40) { Thread.Sleep(25); }
                    }
                    originalGain = session.SimpleAudioVolume.Volume; originalMute = session.SimpleAudioVolume.Mute;
                    session.SimpleAudioVolume.Volume = 1; session.SimpleAudioVolume.Mute = false;
                    Console.Out.NewLine = "\n";
                    Console.Out.WriteLine("ready"); Console.Out.Flush();
                    var command = ReadCommand();
                    WaitForCommand(command);
                    if (command.Result is -1 or 's') return 0;
                    if (command.Result != 'p') throw new InvalidDataException("Expected a one-character play or stop command.");
                    timeout.Token.ThrowIfCancellationRequested();
                    source.AddSamples(bytes, 0, bytes.Length);
                    source.ReadFully = false;
                    command = ReadCommand();
                    var completed = Task.WhenAny(stopped.Task, command).WaitAsync(timeout.Token).GetAwaiter().GetResult();
                    if (completed == command)
                    {
                        if (command.Result is -1 or 's') return 0;
                        throw new InvalidDataException("The setup chime can only be played once.");
                    }
                    stopped.Task.GetAwaiter().GetResult();
                    Console.Out.WriteLine("played"); Console.Out.Flush();
                    return 0;
                }
                finally
                {
                    player.Stop();
                    if (session is not null)
                    {
                        try
                        {
                            if (session.SimpleAudioVolume.Volume == 1 && !session.SimpleAudioVolume.Mute)
                            { session.SimpleAudioVolume.Volume = originalGain; session.SimpleAudioVolume.Mute = originalMute; }
                        }
                        finally { session.Dispose(); }
                    }
                }

                // Console's synchronized reader can block even through ReadAsync; keep the bounded waits outside it.
                Task<int> ReadCommand() => Task.Run(() => Console.In.Read());
                void WaitForCommand(Task<int> command)
                {
                    if (Task.WhenAny(stopped.Task, command).WaitAsync(timeout.Token).GetAwaiter().GetResult() == stopped.Task)
                    {
                        stopped.Task.GetAwaiter().GetResult();
                        throw new IOException("The setup source stopped before the play command.");
                    }
                }
            }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(DiagnosticText.Protect($"Setup confirmation source failed: {error.Message}"));
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
