using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirFlash.App.Ui;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class NativeSetupSmoke
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var output = Path.GetFullPath(NativeOutputCheck.Argument(args, "--output"));
        var directory = Path.GetDirectoryName(output)!; Directory.CreateDirectory(directory);
        var platform = new SimulatedPlatform();
        await using var flow = new NativeSetupFlow(platform);
        var window = new NativeSetupWindow(flow, initializeOnLoad: false);
        var checks = new List<string>();
        try
        {
            await flow.RefreshAsync(default);
            window.RefreshView(); window.Show();
            await Capture("driver", "正在安装音频驱动");
            platform.Installed = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("receiver", "选择 HomePod");
            var connect = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "连接"));
            ((IInvokeProvider)new ButtonAutomationPeer(connect).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("请先选择", StringComparison.Ordinal)))
                throw new InvalidOperationException("An empty device selection must keep an actionable message visible.");
            checks.Add("empty-selection-remains-visible");
            await flow.ConnectAsync(platform.Receiver, default); window.RefreshView();
            await Capture("listening", "听到声音了吗");
            var choices = Descendants(window).OfType<Button>().Select(button => button.Content).ToArray();
            if (!choices.OrderBy(value => value).SequenceEqual(new[] { "关于", "再播放一次", "听到了", "没听到" }.OrderBy(value => value)))
                throw new InvalidOperationException("Confirmation must contain replay, the two hearing choices and About only.");
            if (Descendants(window).OfType<TextBlock>().Sum(text => text.Text.Length) > 100)
                throw new InvalidOperationException("The confirmation screen contains unnecessary explanatory text.");
            checks.Add("confirmation-keeps-only-essential-content");
            var beforeReplay = platform.PlayCount;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            platform.Playback = finished.Task;
            Invoke("再播放一次");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (flow.Step != NativeSetupStep.Testing || Descendants(window).OfType<Button>()
                .Any(button => !Equals(button.Content, "关于") && button.IsEnabled))
                throw new InvalidOperationException("Playback must disable replay and confirmation until it finishes.");
            finished.SetResult();
            await UntilIdle();
            platform.Playback = null;
            Invoke("再播放一次"); await UntilIdle();
            if (platform.PlayCount != beforeReplay + 2 || platform.Completed)
                throw new InvalidOperationException("The replay button must play again without completing setup.");
            checks.Add("replay-button-twice-and-no-overlap");
            window.Width = window.MinWidth;
            await Capture("compact-confirmation", "听到声音了吗");
            window.Width = 480;
            if (platform.Completed) throw new InvalidOperationException("Setup completed before the user confirmed music.");
            await flow.ConfirmMusicAsync(default); window.RefreshView();
            await Capture("ready", "已准备好");
            platform.RenamedExisting = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("renamed-ready", "客厅 HomePod");
            if (!platform.Completed || !platform.Previewed) throw new InvalidOperationException("The setup did not use explicit preview and confirmation.");
            checks.Add("completion-requires-automatic-audio-confirmation");
            platform.RenamedExisting = platform.Completed = platform.Configured = platform.AudioPlayed = false;
            await flow.StartAutomaticAsync(default); window.RefreshView();
            if (flow.Step != NativeSetupStep.Listening || !platform.Configured || !platform.AudioPlayed || platform.Completed)
                throw new InvalidOperationException("A single HomePod must automatically configure and play before the only functional confirmation.");
            await Capture("automatic-confirmation", "听到声音了吗");
            checks.Add("single-device-needs-no-manual-route-name-or-player");
            App.WriteOutput(args, new { ok = true, checks, audio_sent = false, hardware_used = false, production_state_modified = false });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        finally { window.Close(); }

        void Invoke(string label)
        {
            var button = Descendants(window).OfType<Button>().Single(value => Equals(value.Content, label));
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        async Task UntilIdle()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (flow.Step == NativeSetupStep.Listening && Descendants(window).OfType<Button>()
                    .Any(button => Equals(button.Content, "再播放一次") && button.IsEnabled)) return;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("Replay did not return to an enabled confirmation screen.");
        }

        async Task Capture(string name, string expected)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var elements = Descendants(window).ToArray();
            if (!elements.OfType<TextBlock>().Any(text => text.Text.Contains(expected, StringComparison.Ordinal)))
                throw new InvalidOperationException($"The {name} screen is missing its instruction.");
            if (elements.OfType<TextBox>().Any() || elements.OfType<Slider>().Any())
                throw new InvalidOperationException("The first-run wizard must not ask for transport parameters or provide daily volume controls.");
            if (window.ActualWidth is < 420 or > 520 || window.ActualHeight is < 300 or > 460)
                throw new InvalidOperationException("The setup window must remain compact.");
            foreach (var button in elements.OfType<Button>())
            {
                var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                if (bounds.Left < 0 || bounds.Right > window.ActualWidth || bounds.Top < 0 || bounds.Bottom > window.ActualHeight)
                    throw new InvalidOperationException("An action is clipped or outside the compact window.");
            }
            var client = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(client);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "setup-" + name + ".png"))) encoder.Save(file);
            checks.Add(name + "-visible-and-no-technical-inputs");
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class SimulatedPlatform : INativeSetupPlatform
    {
        internal bool Installed, Previewed, Completed, RenamedExisting, Configured, AudioPlayed;
        internal int PlayCount;
        internal Task? Playback;
        internal readonly Receiver Receiver = new("9efd9099abd6", "da", "127.0.0.1")
            { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
        private readonly EndpointIdentity _endpoint = new("simulated-output", "simulated-adapter", NativeBindingPolicy.HardwareId,
            NativeBindingPolicy.Provider, NativeBindingPolicy.DriverVersion, NativeBindingPolicy.DriverSha256, "CABLE Input",
            "HomePod – da", "simulated-format", 2, AudioEndpointKind.Playback, true, "HomePod – da");
        public Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation)
        {
            var live = RenamedExisting ? _endpoint with { FriendlyName = "客厅 HomePod", EndpointLabel = "客厅 HomePod" } : _endpoint;
            var stored = RenamedExisting ? new NativeBinding(1, Receiver.Id, _endpoint, "CABLE Input", "HomePod – da",
                new(false, _endpoint.MixFormat, _endpoint.DriverSha256, 0, 50), 100) : null;
            return Task.FromResult(new NativeSetupInspection(Installed, Installed, Installed ? live : null, stored, Completed));
        }
        public Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<Receiver>>([Receiver]);
        public Task<NativeDriverInstallOutcome> InstallDriverAsync(CancellationToken cancellation) { Installed = true; return Task.FromResult(NativeDriverInstallOutcome.VerificationRequired); }
        public Task PrepareOutputAsync(Receiver receiver, CancellationToken cancellation) { Configured = true; return Task.CompletedTask; }
        public Task PlayConfirmationAsync(CancellationToken cancellation) { AudioPlayed = true; PlayCount++; return Playback?.WaitAsync(cancellation) ?? Task.CompletedTask; }
        public Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation) { Previewed = true; return Task.CompletedTask; }
        public Task CompleteAsync(CancellationToken cancellation) { Completed = true; return Task.CompletedTask; }
        public Task StopPreviewAsync(CancellationToken cancellation) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
