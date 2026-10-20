using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class NativeSetupSmoke
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var output = Path.GetFullPath(NativeOutputMaintenance.Argument(args, "--output"));
        var directory = Path.GetDirectoryName(output)!; Directory.CreateDirectory(directory);
        var platform = new SimulatedPlatform();
        await using var flow = new NativeSetupFlow(platform);
        var window = new NativeSetupWindow(flow, initializeOnLoad: false);
        var checks = new List<string>();
        try
        {
            if (args.Contains("--preview"))
            {
                platform.Installed = true;
                await flow.StartAutomaticAsync(default);
                window.RefreshView();
                window.Title = "AppleHome · " + L.Get("UI preview");
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await Capture("preview-listening", "Did you hear it?");
                App.WriteOutput(args, new { ok = true, preview_only = true, audio_sent = false, hardware_used = false,
                    production_state_modified = false, state = flow.Step.ToString(), data_directory = AppPaths.DataDirectory });
                await closed.Task;
                return 0;
            }
            await flow.RefreshAsync(default);
            window.RefreshView(); window.Show();
            await Capture("driver", "Installing the audio driver…");
            platform.Installed = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("receiver", "Choose a HomePod to connect.");
            var connect = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, L.Get("Connect")));
            ((IInvokeProvider)new ButtonAutomationPeer(connect).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains(L.Get("Select a HomePod to connect."), StringComparison.Ordinal)))
                throw new InvalidOperationException("An empty device selection must keep an actionable message visible.");
            checks.Add("empty-selection-remains-visible");
            await flow.ConnectAsync(platform.Receiver, default); window.RefreshView();
            await Capture("listening", "Did you hear it?");
            var choices = Descendants(window).OfType<Button>().Select(button => button.Content).ToArray();
            if (!choices.OrderBy(value => value).SequenceEqual(new[] { "About", "Play again", "I heard it", "No audio" }.Select(L.Get).OrderBy(value => value)))
                throw new InvalidOperationException("Confirmation must contain replay, the two hearing choices and About only.");
            if (Descendants(window).OfType<TextBlock>().Sum(text => text.Text.Length) > 100)
                throw new InvalidOperationException("The confirmation screen contains unnecessary explanatory text.");
            var liveStatus = (TextBlock)window.FindName("StepText");
            if (AutomationProperties.GetLiveSetting(liveStatus) != AutomationLiveSetting.Polite
                || !AutomationProperties.GetName(liveStatus).Contains(L.Get("Did you hear it?"), StringComparison.Ordinal))
                throw new InvalidOperationException("State changes must have one polite, meaningful automation announcement.");
            checks.Add("state-announcement-is-polite-and-current");
            checks.Add("confirmation-keeps-only-essential-content");
            var replay = (Button)window.FindName("ReplayButton");
            var primary = (Button)window.FindName("PrimaryButton");
            var secondary = (Button)window.FindName("SecondaryButton");
            if (window.Width != 560 || window.Height != 464 || replay.ActualHeight < 38 || primary.ActualHeight < 38
                || primary.ActualWidth < 112 || secondary.ActualWidth < 88
                || ((FrameworkElement)window.FindName("DeviceArtwork")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("The approved device-centered layout and button targets must be present.");
            if (((TextBlock)window.FindName("TaskTitle")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The hearing question must not have a redundant task heading.");
            foreach (var button in new[] { replay, primary, secondary })
            {
                var foreground = ((SolidColorBrush)button.Foreground).Color;
                var background = ((SolidColorBrush)button.Background).Color;
                var light = Luminance(foreground); var dark = Luminance(background);
                if ((Math.Max(light, dark) + 0.05) / (Math.Min(light, dark) + 0.05) < 4.5)
                    throw new InvalidOperationException("Button labels must retain readable contrast.");
            }
            checks.Add("approved-layout-and-button-contrast");
            replay.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!replay.IsKeyboardFocused || ((Border)replay.Template.FindName("FocusRing", replay)).Opacity != 1)
                throw new InvalidOperationException("Keyboard focus needs a visible ring outside the button.");
            await Capture("keyboard-focus", "Did you hear it?");
            checks.Add("keyboard-focus-ring-visible");
            var beforeReplay = platform.PlayCount;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            platform.Playback = finished.Task;
            Invoke("Play again");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (flow.Step != NativeSetupStep.Testing || Descendants(window).OfType<Button>()
                .Any(button => !Equals(button.Content, L.Get("About")) && button.IsEnabled))
                throw new InvalidOperationException("Playback must disable replay and confirmation until it finishes.");
            await Capture("playing", "Playing audio…");
            finished.SetResult();
            await UntilIdle();
            platform.Playback = null;
            Invoke("Play again"); await UntilIdle();
            if (platform.PlayCount != beforeReplay + 2 || platform.Completed)
                throw new InvalidOperationException("The replay button must play again without completing setup.");
            checks.Add("replay-button-twice-and-no-overlap");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Capture("compact-confirmation", "Did you hear it?");
            var compactReplay = replay.TransformToAncestor(window).TransformBounds(new Rect(0, 0, replay.ActualWidth, replay.ActualHeight));
            var compactPrimary = primary.TransformToAncestor(window).TransformBounds(new Rect(0, 0, primary.ActualWidth, primary.ActualHeight));
            if (compactReplay.Bottom + 4 > compactPrimary.Top)
                throw new InvalidOperationException("Compact setup must keep replay visible above the pinned decisions.");
            window.Width = 560; window.Height = 464;
            if (platform.Completed) throw new InvalidOperationException("Setup completed before the user confirmed music.");
            await flow.ConfirmMusicAsync(default); window.RefreshView();
            await Capture("ready", "Ready to play");
            platform.RenamedExisting = true;
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("renamed-ready", "客厅 HomePod");
            if (!platform.Completed || !platform.Previewed) throw new InvalidOperationException("The setup did not use explicit preview and confirmation.");
            checks.Add("completion-requires-automatic-audio-confirmation");
            platform.RenamedExisting = platform.Completed = platform.Configured = platform.AudioPlayed = false;
            await flow.StartAutomaticAsync(default); window.RefreshView();
            if (flow.Step != NativeSetupStep.Listening || !platform.Configured || !platform.AudioPlayed || platform.Completed)
                throw new InvalidOperationException("A single HomePod must automatically configure and play before the only functional confirmation.");
            await Capture("automatic-confirmation", "Did you hear it?");
            checks.Add("single-device-needs-no-manual-route-name-or-player");
            platform.DiscoveryError = new IOException("设备发现失败：网络不存在或尚未启动。可手动添加接收器。");
            await flow.RefreshAsync(default); window.RefreshView();
            await Capture("discovery-unavailable", "Can't find HomePod");
            if (Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("接收器", StringComparison.Ordinal)
                || text.Text.Contains("手动添加", StringComparison.Ordinal) || text.Text.Contains("网络不存在", StringComparison.Ordinal)))
                throw new InvalidOperationException("Discovery errors must use HomePod language and supported recovery only.");
            platform.DiscoveryError = null;
            Invoke("Find HomePod");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (flow.Step != NativeSetupStep.Listening)
                throw new InvalidOperationException("Search recovery must retry the automatic HomePod connection.");
            checks.Add("discovery-failure-retries-without-manual-device-setup");
            platform.Receiver = platform.Receiver with { Name = new string('长', 160) };
            await flow.StartAutomaticAsync(default); window.RefreshView();
            await Capture("long-device-name", "Did you hear it?");
            var title = (TextBlock)window.FindName("HeaderTitle");
            if (title.Text.Length > 96 || title.TextTrimming != TextTrimming.CharacterEllipsis)
                throw new InvalidOperationException("Long device names must remain bounded and visibly trimmed.");
            window.ApplyHighContrastPalette(); window.RefreshView();
            await Capture("system-colors", "Did you hear it?");
            if (!ReferenceEquals(window.Resources["SetupReplayFill"], SystemColors.WindowBrush)
                || !ReferenceEquals(window.Resources["SetupReplayInk"], SystemColors.WindowTextBrush)
                || !ReferenceEquals(window.Resources["SetupAccentText"], SystemColors.HighlightTextBrush)
                || ((FrameworkElement)window.FindName("DeviceArtwork")).Visibility != Visibility.Collapsed
                || ((FrameworkElement)window.FindName("GenericArtwork")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("System contrast colors must replace the pale artwork and control palette.");
            checks.Add("system-palette-and-monochrome-artwork");
            if (!string.Equals(window.Language.IetfLanguageTag, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Setup accessibility language must follow the selected Windows UI language.");
            checks.Add("setup-language-follows-ui-culture");
            App.WriteOutput(args, new { ok = true, checks, language = CultureInfo.CurrentUICulture.Name, layout_language = window.Language.IetfLanguageTag,
                system_default = !args.Contains("--ui-language"), audio_sent = false, hardware_used = false, production_state_modified = false });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        finally { window.Close(); }

        void Invoke(string label)
        {
            var button = Descendants(window).OfType<Button>().Single(value => Equals(value.Content, L.Get(label)));
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        async Task UntilIdle()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (flow.Step == NativeSetupStep.Listening && Descendants(window).OfType<Button>()
                    .Any(button => Equals(button.Content, L.Get("Play again")) && button.IsEnabled)) return;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("Replay did not return to an enabled confirmation screen.");
        }

        async Task Capture(string name, string expected)
        {
            expected = L.Get(expected);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var elements = Descendants(window).ToArray();
            if (!elements.OfType<TextBlock>().Any(text => text.Text.Contains(expected, StringComparison.Ordinal)))
                throw new InvalidOperationException($"The {name} screen is missing its instruction.");
            if (elements.OfType<TextBox>().Any() || elements.OfType<Slider>().Any())
                throw new InvalidOperationException("The first-run wizard must not ask for transport parameters or provide daily volume controls.");
            if (window.ActualWidth is < 420 or > 560 || window.ActualHeight is < 300 or > 464)
                throw new InvalidOperationException("The setup window must remain compact.");
            foreach (var button in elements.OfType<Button>().Where(button => button.IsVisible))
            {
                var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                if (bounds.Left < 0 || bounds.Right > window.ActualWidth || bounds.Top < 0 || bounds.Bottom > window.ActualHeight)
                    throw new InvalidOperationException("An action is clipped or outside the compact window.");
                if (button.Content is string label)
                {
                    var caption = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize,
                        button.Foreground, VisualTreeHelper.GetDpi(button).PixelsPerDip);
                    var glyph = ReferenceEquals(button, window.FindName("ReplayButton")) ? 18 : 0;
                    if (caption.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right
                        + button.BorderThickness.Left + button.BorderThickness.Right + glyph > button.ActualWidth + 1)
                        throw new InvalidOperationException("A translated action label is clipped.");
                }
            }
            if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en"
                && new[] { "TaskTitle", "StepText", "StatusText" }.Select(id => ((TextBlock)window.FindName(id)).Text)
                    .Concat(elements.OfType<Button>().Select(button => button.Content?.ToString() ?? ""))
                    .Any(text => text.Any(character => character is >= '\u4e00' and <= '\u9fff')))
                throw new InvalidOperationException("English setup must not leak hardcoded Chinese UI text.");
            var client = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(client);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "setup-" + name + ".png"))) encoder.Save(file);
            checks.Add(name + "-visible-and-no-technical-inputs");
        }
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var channel = value / 255.0; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
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
        internal Exception? DiscoveryError;
        internal Receiver Receiver = new("9efd9099abd6", "da", "127.0.0.1")
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
        public Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation) => DiscoveryError is { } error
            ? Task.FromException<IReadOnlyList<Receiver>>(error) : Task.FromResult<IReadOnlyList<Receiver>>([Receiver]);
        public Task<NativeDriverInstallOutcome> InstallDriverAsync(CancellationToken cancellation) { Installed = true; return Task.FromResult(NativeDriverInstallOutcome.VerificationRequired); }
        public Task PrepareOutputAsync(Receiver receiver, CancellationToken cancellation) { Configured = true; return Task.CompletedTask; }
        public Task PlayConfirmationAsync(CancellationToken cancellation) { AudioPlayed = true; PlayCount++; return Playback?.WaitAsync(cancellation) ?? Task.CompletedTask; }
        public Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation) { Previewed = true; return Task.CompletedTask; }
        public Task CompleteAsync(CancellationToken cancellation) { Completed = true; return Task.CompletedTask; }
        public Task StopPreviewAsync(CancellationToken cancellation) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
