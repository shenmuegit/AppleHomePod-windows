using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Ui;

public sealed class NativeSetupWindow : Window
{
    private readonly NativeSetupFlow _flow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _body = new() { Margin = new(28, 22, 28, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0), FontSize = 13 };
    private readonly List<Button> _actions = [];
    private bool _busy, _closing, _allowClose;
    private string? _operationError, _busyTitle;
    public NativeSetupWindow(NativeSetupFlow flow, bool initializeOnLoad = true, bool verifyExistingConnection = false)
    {
        _flow = flow;
        Title = "AppleHome";
        Width = 480; Height = 340; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
        FontFamily = new FontFamily("Satoshi, Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        Resources["SurfaceBrush"] = SystemColors.WindowBrush; Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedBrush"] = SystemColors.GrayTextBrush;
        if (SystemParameters.HighContrast)
        { Resources["TintBrush"] = SystemColors.ControlBrush; Resources["LineBrush"] = SystemColors.WindowTextBrush; }
        AppIconService.Track(this);
        var layout = new DockPanel();
        var about = new Button { Content = "关于", FontSize = 12, BorderThickness = new(0), Background = Brushes.Transparent,
            Foreground = SystemColors.GrayTextBrush, Padding = new(0), MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(28, 0, 28, 14) };
        about.Click += (_, _) => ShowAbout();
        DockPanel.SetDock(about, Dock.Bottom); layout.Children.Add(about);
        layout.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
        Closing += CloseSafely;
        _flow.Changed += FlowChanged;
        Loaded += (_, _) => {
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                _body.BeginAnimation(OpacityProperty, new DoubleAnimation(.9, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        };
        if (initializeOnLoad) Loaded += async (_, _) => await RunAsync(async token => {
            await _flow.StartAutomaticAsync(token);
            if (verifyExistingConnection && _flow.Step == NativeSetupStep.Ready && _flow.SelectedReceiver is { } receiver)
                await _flow.ConnectAsync(receiver, token);
        });
        RefreshView();
    }
    public void RefreshView()
    {
        _body.Children.Clear();
        _actions.Clear();
        Height = _flow.Step == NativeSetupStep.Receiver ? 420 : 340;
        if (_flow.SelectedReceiver is { } selected && _flow.Step != NativeSetupStep.Ready)
            _body.Children.Add(new TextBlock { Text = selected.Name, Foreground = SystemColors.GrayTextBrush,
                FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 0, 6) });
        Text(_busyTitle ?? (_flow.Step switch {
            NativeSetupStep.Driver => "正在安装音频驱动",
            NativeSetupStep.Restart => "重启后继续",
            NativeSetupStep.Receiver => "选择 HomePod",
            NativeSetupStep.Configuring => "正在连接…",
            NativeSetupStep.Testing => "正在播放…",
            NativeSetupStep.Listening => "听到声音了吗？",
            NativeSetupStep.Ready => "已准备好",
            NativeSetupStep.Blocked => "连接未完成",
            _ => "正在准备…"
        }), 26, FontWeights.SemiBold);
        switch (_flow.Step)
        {
            case NativeSetupStep.Driver:
                Text("如 Windows 提示，请允许安装。");
                if (!_busy) AsyncAction("开始自动设置", _flow.StartAutomaticAsync);
                break;
            case NativeSetupStep.Restart:
                Text("保存工作，重启 Windows 后打开 AppleHome。");
                AsyncAction("重新检查", _flow.RefreshAsync);
                break;
            case NativeSetupStep.Receiver:
                var devices = new ListBox { MinHeight = 70, MaxHeight = 140, Margin = new(0, 12, 0, 12) };
                foreach (var receiver in _flow.Receivers)
                {
                    var qualified = receiver.PrefersNtpAlac && receiver.Online;
                    devices.Items.Add(new ListBoxItem { Content = receiver.Name + (qualified ? "" : " · 暂不支持"),
                        Tag = receiver, Padding = new(12), IsEnabled = qualified });
                }
                if (devices.Items.Count == 0) Text("未发现设备，请确认 HomePod 已通电并与电脑同网。");
                _body.Children.Add(devices);
                AsyncAction("连接", token => devices.SelectedItem is ListBoxItem { Tag: Receiver receiver }
                    ? _flow.ConnectAsync(receiver, token) : ShowSelectionRequired());
                AsyncAction("重新搜索", _flow.RefreshAsync);
                break;
            case NativeSetupStep.Configuring:
                break;
            case NativeSetupStep.Testing:
            case NativeSetupStep.Listening:
                ConfirmationActions();
                break;
            case NativeSetupStep.Ready:
                Text(OutputName());
                Action("完成", Close);
                break;
            case NativeSetupStep.Blocked:
                AsyncAction("重试", _flow.RefreshAsync);
                break;
            default:
                break;
        }
        _status.Text = _operationError ?? _flow.Error ?? "";
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _body.Children.Add(_status);
        foreach (var button in _actions) button.IsEnabled = !_busy;
    }
    private void ConfirmationActions()
    {
        var replay = AsyncAction("再播放一次", _flow.ReplayConfirmationAsync);
        replay.BorderThickness = new(0); replay.Background = Brushes.Transparent;
        replay.Foreground = SystemParameters.HighContrast ? SystemColors.HotTrackBrush : new SolidColorBrush(Color.FromRgb(31, 76, 120));
        replay.MinWidth = 0; replay.Padding = new(0); replay.Margin = new(0, 14, 0, 12);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0) };
        var no = AsyncAction("没听到", _flow.RejectMusicAsync);
        var yes = AsyncAction("听到了", async token => { await _flow.ConfirmMusicAsync(token); if (_flow.Step == NativeSetupStep.Ready) Close(); }, "正在完成…");
        _body.Children.Remove(no); _body.Children.Remove(yes);
        no.Margin = new(0, 0, 10, 0); yes.Margin = new(0);
        yes.Background = yes.BorderBrush = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(31, 76, 120));
        yes.Foreground = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White;
        yes.Resources["TintBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(23, 60, 95));
        row.Children.Add(no); row.Children.Add(yes); _body.Children.Add(row);
    }
    private void FlowChanged()
    {
        if (_closing) return;
        if (Dispatcher.CheckAccess()) Update(); else _ = Dispatcher.BeginInvoke(Update);
        void Update() { if (!_closing) { _operationError = null; RefreshView(); } }
    }
    private string OutputName()
    {
        var endpoint = _flow.Inspection?.Endpoint;
        if (!string.IsNullOrWhiteSpace(endpoint?.EndpointLabel)) return endpoint.EndpointLabel;
        if (!string.IsNullOrWhiteSpace(endpoint?.FriendlyName)) return endpoint.FriendlyName;
        return _flow.Inspection?.Binding?.AssignedName ?? (_flow.SelectedReceiver is { } receiver ? NativeSetupFlow.OutputName(receiver) : "HomePod");
    }
    private static Task ShowSelectionRequired() => Task.FromException(new InvalidOperationException("请先选择一只可连接的 HomePod。"));
    private void Text(string text, double size = 14, FontWeight? weight = null)
        => _body.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), LineHeight = size * 1.4 });
    private void Action(string title, System.Action action)
    {
        var button = Button(title); button.Click += (_, _) => { try { action(); } catch (Exception error) { _status.Text = DiagnosticText.Protect(error.Message); } };
    }
    private Button AsyncAction(string title, Func<CancellationToken, Task> action, string? busyTitle = null)
    { var button = Button(title); button.Click += async (_, _) => await RunAsync(action, busyTitle, title); return button; }
    private Button Button(string title)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120,
            Padding = new(18, 9, 18, 9), Margin = new(0, 6, 0, 2) };
        _actions.Add(button); _body.Children.Add(button); return button;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, string? busyTitle = null, string? focusAfter = null)
    {
        if (_busy || _closing) return;
        var hadFocus = IsKeyboardFocusWithin;
        _busy = true; _operationError = null; _busyTitle = busyTitle;
        foreach (var button in _actions) button.IsEnabled = false;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { AppPaths.Log("First-run setup failed: " + error); _operationError = DiagnosticText.Protect(error.Message); }
        finally
        {
            _busy = false; _busyTitle = null;
            if (!_closing)
            {
                RefreshView();
                if (hadFocus && IsActive && _actions.FirstOrDefault(button => Equals(button.Content, focusAfter) && button.IsEnabled) is { } button)
                    Keyboard.Focus(button);
            }
        }
    }
    private void ShowAbout()
    {
        var about = new Window { Title = "关于 AppleHome", Owner = this, Width = 400, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush, FontFamily = FontFamily, FontSize = 14 };
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = "AppleHome · GPLv3+\n\n音频驱动：VB-CABLE · VB-Audio Software\nDonationware", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var license = new Button { Content = "驱动许可与捐助", HorizontalAlignment = HorizontalAlignment.Left };
        license.Click += (_, _) => { try { Open("https://vb-audio.com/Services/licensing.htm"); } catch (Exception error) { MessageBox.Show(about, DiagnosticText.Protect(error.Message)); } };
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
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
