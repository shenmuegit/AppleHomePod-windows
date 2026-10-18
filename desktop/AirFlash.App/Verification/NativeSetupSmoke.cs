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
            await Capture("driver", "官方驱动页面");
            platform.Installed = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("receiver", "选择 HomePod");
            var connect = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "连接并验证"));
            ((IInvokeProvider)new ButtonAutomationPeer(connect).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("请先选择", StringComparison.Ordinal)))
                throw new InvalidOperationException("An empty device selection must keep an actionable message visible.");
            checks.Add("empty-selection-remains-visible");
            await flow.ConnectAsync(platform.Receiver, default); window.RefreshView();
            await Capture("listening", "正常音乐");
            if (platform.Completed) throw new InvalidOperationException("Setup completed before the user confirmed music.");
            await flow.ConfirmMusicAsync(default); window.RefreshView();
            await Capture("ready", "已准备好");
            platform.RenamedExisting = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("renamed-ready", "客厅 HomePod");
            if (!platform.Completed || !platform.Previewed) throw new InvalidOperationException("The setup did not use explicit preview and confirmation.");
            checks.Add("completion-requires-normal-music-confirmation");
            App.WriteOutput(args, new { ok = true, checks, audio_sent = false, hardware_used = false, production_state_modified = false });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        finally { window.Close(); }

        async Task Capture(string name, string expected)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var elements = Descendants(window).ToArray();
            if (!elements.OfType<TextBlock>().Any(text => text.Text.Contains(expected, StringComparison.Ordinal)))
                throw new InvalidOperationException($"The {name} screen is missing its instruction.");
            if (elements.OfType<TextBox>().Any() || elements.OfType<Slider>().Any())
                throw new InvalidOperationException("The first-run wizard must not ask for transport parameters or provide daily volume controls.");
            if (window.ActualWidth < 600 || window.ActualHeight < 500) throw new InvalidOperationException("The setup window did not lay out.");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
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
        internal bool Installed, Previewed, Completed, RenamedExisting;
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
        public Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation) { Previewed = true; return Task.CompletedTask; }
        public Task CompleteAsync(CancellationToken cancellation) { Completed = true; return Task.CompletedTask; }
        public Task StopPreviewAsync(CancellationToken cancellation) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
