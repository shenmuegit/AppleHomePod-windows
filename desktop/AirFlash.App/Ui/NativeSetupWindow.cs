using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Ui;

public partial class NativeSetupWindow : Window
{
    private readonly NativeSetupFlow _flow;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _closing, _allowClose, _confirming;
    private string? _operationError, _selectedId, _lastAnnouncement;
    public NativeSetupWindow(NativeSetupFlow flow, bool initializeOnLoad = true, bool verifyExistingConnection = false)
    {
        _flow = flow;
        InitializeComponent();
        if (SystemParameters.HighContrast)
        {
            Resources["SetupPaper"] = SystemColors.WindowBrush; Resources["SetupFooter"] = SystemColors.ControlBrush;
            Resources["SetupCanvas"] = SystemColors.ControlBrush;
            Resources["SetupInk"] = SystemColors.WindowTextBrush; Resources["SetupMuted"] = SystemColors.WindowTextBrush;
            Resources["SetupLine"] = SystemColors.WindowTextBrush; Resources["SetupAccent"] = SystemColors.HighlightBrush;
            Resources["SetupAccentText"] = SystemColors.HighlightTextBrush;
            Resources["SetupHover"] = SystemColors.ControlBrush; Resources["SetupAccentHover"] = SystemColors.HighlightBrush;
            Resources["SetupPressed"] = SystemColors.HighlightBrush; Resources["SetupPressedText"] = SystemColors.HighlightTextBrush;
            Resources["SetupAccentPressed"] = SystemColors.HighlightBrush;
            Resources["SetupButtonFill"] = SystemColors.WindowBrush; Resources["SetupButtonBorder"] = SystemColors.WindowTextBrush;
        }
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
    public void RefreshView()
    {
        var step = _flow.Step;
        var listening = step is NativeSetupStep.Testing or NativeSetupStep.Listening;
        HeaderTitle.Text = _flow.SelectedReceiver is { } boundReceiver ? DisplayName(boundReceiver.Name) : "HomePod";
        TaskTitle.Text = step switch {
            NativeSetupStep.Driver => "安装音频驱动", NativeSetupStep.Restart => "重启后继续",
            NativeSetupStep.Ready => "已准备好", NativeSetupStep.Blocked => "连接未完成",
            NativeSetupStep.DiscoveryUnavailable => "暂时无法查找 HomePod",
            _ when listening => "测试声音", _ => "连接设备"
        };
        DeviceText.Text = step == NativeSetupStep.Ready ? OutputName() : _flow.SelectedReceiver is { } selected
            ? (selected.Model == "AudioAccessory5,1" ? "HomePod mini" : "Windows 音频输出")
            : "Windows 音频输出";
        StepText.Text = _confirming ? "正在保存设置…" : step switch {
            NativeSetupStep.Driver => "正在安装音频驱动…",
            NativeSetupStep.Restart => "保存工作，重启 Windows 后重新打开 AppleHome。",
            NativeSetupStep.Receiver when _flow.Receivers.Count == 0 => "未发现 HomePod，请确认设备已通电并与电脑同网。",
            NativeSetupStep.Receiver => "选择要连接的 HomePod。",
            NativeSetupStep.Configuring => "正在准备连接和声音…",
            NativeSetupStep.Testing => "正在播放，请留意 HomePod。",
            NativeSetupStep.Listening => "听到声音了吗？",
            NativeSetupStep.Ready => "已可播放音乐和视频。",
            NativeSetupStep.Blocked => "请检查下方原因，然后重试。",
            NativeSetupStep.DiscoveryUnavailable => "请确认 HomePod 已通电，并与电脑连接同一网络。",
            _ => "正在检查设备…"
        };
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
                var item = new ListBoxItem { Content = DisplayName(receiver.Name) + (qualified ? "" : " · 暂不支持"),
                    Tag = receiver, Padding = new(10), IsEnabled = qualified };
                ReceiverPicker.Items.Add(item);
                if (receiver.Id == previous) ReceiverPicker.SelectedItem = item;
            }
        }
        ReplayButton.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Visibility = listening || step == NativeSetupStep.Receiver ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Content = listening ? "没听到" : "重新搜索";
        PrimaryButton.Content = step switch {
            NativeSetupStep.Driver => "开始设置", NativeSetupStep.Restart => "重新检查",
            NativeSetupStep.Receiver => "连接", NativeSetupStep.Ready => "完成",
            NativeSetupStep.Blocked => "重试", NativeSetupStep.DiscoveryUnavailable => "重新查找", _ => "听到了"
        };
        PrimaryButton.Visibility = listening || step is NativeSetupStep.Driver or NativeSetupStep.Restart or NativeSetupStep.Ready or NativeSetupStep.Blocked
            or NativeSetupStep.DiscoveryUnavailable
            || showReceivers ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_busy && !_closing;
        ReplayButton.IsEnabled = enabled; PrimaryButton.IsEnabled = enabled; SecondaryButton.IsEnabled = enabled;
        ReceiverPicker.IsEnabled = enabled;
        StatusText.Text = _operationError ?? _flow.Error ?? (step == NativeSetupStep.Driver ? "如 Windows 提示，请允许安装。" : "");
        StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
                ? _flow.ConnectAsync(receiver, token) : Task.FromException(new InvalidOperationException("请先选择一只可连接的 HomePod。")), PrimaryButton);
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
        var about = new Window { Title = "关于 AppleHome", Owner = this, Width = 400, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = 14 };
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = "AppleHome · GPLv3+\n\n音频驱动：VB-CABLE · VB-Audio Software\nDonationware", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var license = new Button { Content = "驱动许可与捐助", Style = (Style)FindResource("SetupButton"), HorizontalAlignment = HorizontalAlignment.Left };
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
