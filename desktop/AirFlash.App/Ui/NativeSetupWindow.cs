using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Ui;

public partial class NativeSetupWindow : Window
{
    private readonly NativeSetupFlow _flow;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _closing, _allowClose, _confirming, _highContrast;
    private string? _operationError, _selectedId, _lastAnnouncement;
    public NativeSetupWindow(NativeSetupFlow flow, bool initializeOnLoad = true, bool verifyExistingConnection = false)
    {
        _flow = flow;
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.Name);
        if (SystemParameters.HighContrast) ApplyHighContrastPalette();
        SizeChanged += (_, _) => RefreshPresentation();
        AppIconService.Track(this);
        Closing += CloseSafely;
        _flow.Changed += FlowChanged;
        if (initializeOnLoad) Loaded += async (_, _) => await RunAsync(async token => {
            await _flow.StartAutomaticAsync(token);
            if (verifyExistingConnection && _flow.Step == NativeSetupStep.Ready && _flow.SelectedReceiver is { } receiver)
                await _flow.ConnectAsync(receiver, token);
        });
        RefreshView();
    }
    internal void ApplyHighContrastPalette()
    {
        _highContrast = true;
        foreach (var key in new[] { "SetupPaper", "SetupCanvas", "SetupFooter", "SetupButtonFill", "SetupReplayFill", "SetupReplayHover" })
            Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "SetupInk", "SetupMuted", "SetupLine", "SetupButtonInk", "SetupButtonBorder", "SetupReplayBorder", "SetupReplayInk" })
            Resources[key] = SystemColors.WindowTextBrush;
        foreach (var key in new[] { "SetupAccent", "SetupAccentBorder", "SetupAccentHover", "SetupAccentPressed", "SetupPressed", "SetupReplayPressed" })
            Resources[key] = SystemColors.HighlightBrush;
        foreach (var key in new[] { "SetupAccentText", "SetupPressedText", "SetupReplayPressedText" })
            Resources[key] = SystemColors.HighlightTextBrush;
        Resources["SetupHover"] = SystemColors.ControlBrush;
        Resources["SetupHoverText"] = SystemColors.ControlTextBrush;
    }
    private void RefreshPresentation()
    {
        var compact = (ActualHeight > 0 ? ActualHeight : Height) < 430;
        var listening = _flow.Step is NativeSetupStep.Testing or NativeSetupStep.Listening;
        var artwork = !_highContrast && _flow.SelectedReceiver?.Model == "AudioAccessory5,1";
        DeviceArtwork.Visibility = artwork ? Visibility.Visible : Visibility.Collapsed;
        GenericArtwork.Visibility = artwork ? Visibility.Collapsed : Visibility.Visible;
        foreach (var view in new[] { DeviceArtwork, GenericArtwork })
        {
            view.Height = compact ? 80 : 106;
            view.Margin = new(0, compact ? 16 : 24, 0, 0);
        }
        HeaderTitle.Margin = new(0, compact ? 8 : 10, 0, 0);
        TaskContent.Margin = new(0, compact ? 20 : 34, 0, 0);
        TaskTitle.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;
        StepText.FontSize = listening ? 22 : 14;
        StepText.FontWeight = listening ? FontWeights.SemiBold : FontWeights.Normal;
        StepText.LineHeight = listening ? 30 : 21;
        StepText.Margin = new(0, listening ? 0 : 10, 0, 0);
        StepText.SetResourceReference(TextBlock.ForegroundProperty, listening ? "SetupInk" : "SetupMuted");
    }
    public void RefreshView()
    {
        var step = _flow.Step;
        var listening = step is NativeSetupStep.Testing or NativeSetupStep.Listening;
        HeaderTitle.Text = _flow.SelectedReceiver is { } boundReceiver ? DisplayName(boundReceiver.Name) : "HomePod";
        TaskTitle.Text = L.Get(step switch {
            NativeSetupStep.Driver => "Install audio driver", NativeSetupStep.Restart => "Restart to continue",
            NativeSetupStep.Ready => "Ready to play", NativeSetupStep.Blocked => "Connection incomplete",
            NativeSetupStep.DiscoveryUnavailable => "Can't find HomePod",
            _ when listening => "Check audio", _ => "Connect HomePod"
        });
        DeviceText.Text = step == NativeSetupStep.Ready ? OutputName() : _flow.SelectedReceiver is { } selected
            ? (selected.Model == "AudioAccessory5,1" ? "HomePod mini" : L.Get("Windows audio output"))
            : L.Get("Windows audio output");
        StepText.Text = L.Get(_confirming ? "Saving settings…" : step switch {
            NativeSetupStep.Driver => "Installing the audio driver…",
            NativeSetupStep.Restart => "Save your work, restart Windows, then reopen AppleHome.",
            NativeSetupStep.Receiver when _flow.Receivers.Count == 0 => "No HomePod found. Check that it's on and connected to the same network.",
            NativeSetupStep.Receiver => "Choose a HomePod to connect.",
            NativeSetupStep.Configuring => "Preparing the connection and audio…",
            NativeSetupStep.Testing => "Playing audio…",
            NativeSetupStep.Listening => "Did you hear it?",
            NativeSetupStep.Ready => "Ready for music and video.",
            NativeSetupStep.Blocked => "Check the details below, then try again.",
            NativeSetupStep.DiscoveryUnavailable => "Check that HomePod is on and connected to the same network as this PC.",
            _ => "Checking devices…"
        });
        Preparation.Visibility = _busy && step is not (NativeSetupStep.Testing or NativeSetupStep.Listening)
            ? Visibility.Visible : Visibility.Collapsed;
        Preparation.IsIndeterminate = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        var showReceivers = step == NativeSetupStep.Receiver && _flow.Receivers.Count > 0;
        ReceiverPicker.Visibility = showReceivers ? Visibility.Visible : Visibility.Collapsed;
        if (showReceivers)
        {
            var previous = _selectedId;
            ReceiverPicker.Items.Clear();
            foreach (var receiver in _flow.Receivers)
            {
                var qualified = receiver.Online && receiver.PrefersNtpAlac;
                var item = new ListBoxItem { Content = DisplayName(receiver.Name) + (qualified ? "" : " · " + L.Get("Not supported yet")),
                    Tag = receiver, Padding = new(10), IsEnabled = qualified };
                ReceiverPicker.Items.Add(item);
                if (receiver.Id == previous) ReceiverPicker.SelectedItem = item;
            }
        }
        ReplayButton.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Visibility = listening || step == NativeSetupStep.Receiver ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Content = L.Get(listening ? "No audio" : "Search again");
        PrimaryButton.Content = L.Get(step switch {
            NativeSetupStep.Driver => "Start setup", NativeSetupStep.Restart => "Check again",
            NativeSetupStep.Receiver => "Connect", NativeSetupStep.Ready => "Done",
            NativeSetupStep.Blocked => "Retry", NativeSetupStep.DiscoveryUnavailable => "Find HomePod", _ => "I heard it"
        });
        PrimaryButton.Visibility = listening || step is NativeSetupStep.Driver or NativeSetupStep.Restart or NativeSetupStep.Ready or NativeSetupStep.Blocked
            or NativeSetupStep.DiscoveryUnavailable
            || showReceivers ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_busy && !_closing;
        ReplayButton.IsEnabled = enabled; PrimaryButton.IsEnabled = enabled; SecondaryButton.IsEnabled = enabled;
        ReceiverPicker.IsEnabled = enabled;
        StatusText.Text = L.Get(_operationError ?? _flow.Error ?? (step == NativeSetupStep.Driver ? "Allow installation if Windows asks." : ""));
        StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshPresentation();
        AnnounceState(StatusText.Text.Length == 0 ? StepText.Text : StepText.Text + " " + StatusText.Text);
    }
    private void AnnounceState(string text)
    {
        if (_lastAnnouncement == text) return;
        _lastAnnouncement = text;
        AutomationProperties.SetName(StepText, text);
        if (IsLoaded && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            (UIElementAutomationPeer.FromElement(StepText) ?? UIElementAutomationPeer.CreatePeerForElement(StepText))?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private static string DisplayName(string name)
    {
        var safe = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return safe.Length > 96 ? safe[..95] + "…" : safe;
    }
    private void ReceiverSelected(object sender, SelectionChangedEventArgs args)
    { if (ReceiverPicker.SelectedItem is ListBoxItem { Tag: Receiver receiver }) _selectedId = receiver.Id; }
    private async void ReplayClicked(object sender, RoutedEventArgs args) => await RunAsync(_flow.ReplayConfirmationAsync, ReplayButton);
    private async void SecondaryClicked(object sender, RoutedEventArgs args)
        => await RunAsync(_flow.Step == NativeSetupStep.Listening ? _flow.RejectMusicAsync : _flow.RefreshAsync, SecondaryButton);
    private async void PrimaryClicked(object sender, RoutedEventArgs args)
    {
        if (_flow.Step == NativeSetupStep.Ready) { Close(); return; }
        if (_flow.Step == NativeSetupStep.Listening)
        {
            _confirming = true;
            try { await RunAsync(async token => { await _flow.ConfirmMusicAsync(token); if (_flow.Step == NativeSetupStep.Ready) Close(); }, PrimaryButton); }
            finally { _confirming = false; if (!_closing) RefreshView(); }
            return;
        }
        if (_flow.Step == NativeSetupStep.Receiver)
            await RunAsync(token => ReceiverPicker.SelectedItem is ListBoxItem { Tag: Receiver receiver }
                ? _flow.ConnectAsync(receiver, token) : Task.FromException(new InvalidOperationException(L.Get("Select a HomePod to connect."))), PrimaryButton);
        else await RunAsync(_flow.StartAutomaticAsync, PrimaryButton);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, Button? focusAfter = null)
    {
        if (_busy || _closing) return;
        var hadFocus = IsKeyboardFocusWithin;
        _busy = true; _operationError = null; RefreshView();
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { AppPaths.Log("First-run setup failed: " + error); _operationError = DiagnosticText.Protect(error.Message); }
        finally
        {
            _busy = false;
            if (!_closing)
            {
                RefreshView();
                if (hadFocus && IsActive && focusAfter is { IsEnabled: true, IsVisible: true }) Keyboard.Focus(focusAfter);
            }
        }
    }
    private void FlowChanged()
    {
        if (_closing) return;
        if (Dispatcher.CheckAccess()) Update(); else _ = Dispatcher.BeginInvoke(Update);
        void Update() { if (!_closing) { _operationError = null; RefreshView(); } }
    }
    private string OutputName() => _flow.Inspection?.Endpoint?.EndpointLabel ?? _flow.Inspection?.Binding?.AssignedName ?? "HomePod";
    private void AboutClicked(object sender, RoutedEventArgs args)
    {
        var about = new Window { Title = L.Get("About AppleHome"), Owner = this, Width = 400, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = 14, Language = Language };
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = L.Get("AppleHome · GPLv3+\n\nAudio driver: VB-CABLE · VB-Audio Software\nDonationware"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var license = new Button { Content = L.Get("Driver license and donations"), Style = (Style)FindResource("SetupButton"), HorizontalAlignment = HorizontalAlignment.Left };
        license.Click += (_, _) => {
            try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Services/licensing.htm") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(about, DiagnosticText.Protect(error.Message)); }
        };
        body.Children.Add(license); about.Content = body; about.ShowDialog();
    }
    private async void CloseSafely(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true; _lifetime.Cancel();
        try { await _flow.DisposeAsync(); }
        catch (Exception error) { AppPaths.Log("First-run setup cleanup failed: " + error); }
        finally { _flow.Changed -= FlowChanged; _lifetime.Dispose(); _allowClose = true; _ = Dispatcher.BeginInvoke(Close); }
    }
}
