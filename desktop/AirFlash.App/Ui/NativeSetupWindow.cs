using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Ui;

public sealed class NativeSetupWindow : Window
{
    private readonly NativeSetupFlow _flow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _body = new() { Margin = new(30, 20, 30, 16) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 0) };
    private bool _busy, _closing, _allowClose;
    private string? _operationError;
    public NativeSetupWindow(NativeSetupFlow flow, bool initializeOnLoad = true, bool verifyExistingConnection = false)
    {
        _flow = flow;
        Title = "AppleHome · 首次安装与连接";
        Width = 760; Height = 650; MinWidth = 620; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 15;
        AppIconService.Track(this);
        var layout = new DockPanel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(24, 8, 24, 18) };
        var close = new Button { Content = "关闭", Padding = new(20, 8, 20, 8), MinWidth = 90 };
        close.Click += (_, _) => Close(); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
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
        _body.Children.Clear();
        Text("将 HomePod 添加为 Windows 输出", 25, FontWeights.SemiBold);
        Text("安装程序自动配置 Windows 输出和连接参数，你只需确认是否听到声音。", 15);
        Text(_flow.Step switch {
            NativeSetupStep.Driver => "正在安装音频驱动",
            NativeSetupStep.Restart => "需要重启 Windows",
            NativeSetupStep.Receiver => "选择要使用的 HomePod",
            NativeSetupStep.Configuring => "正在自动配置声音输出",
            NativeSetupStep.Testing => "正在播放确认音频",
            NativeSetupStep.Listening => "刚才听到 HomePod 的提示音了吗？",
            NativeSetupStep.Ready => "已准备好",
            NativeSetupStep.Blocked => "需要先处理一个问题",
            _ => "正在检查这台电脑"
        }, 21, FontWeights.SemiBold);
        switch (_flow.Step)
        {
            case NativeSetupStep.Driver:
                Text("正在安装内附的原厂 VB-CABLE 驱动。如 Windows 询问安装许可，请核对发布者后允许。安装结束后会自动核验设备。");
                if (!_busy) AsyncAction("开始自动设置", _flow.StartAutomaticAsync);
                break;
            case NativeSetupStep.Restart:
                Text("驱动已安装。请保存工作并重启 Windows，然后重新打开 AppleHome，程序会自动继续。无需再次安装驱动或手动配置输出。");
                AsyncAction("我已重启，重新检查", _flow.RefreshAsync);
                break;
            case NativeSetupStep.Receiver:
                Text("电脑与 HomePod 需要连接同一个局域网。请选择要使用的 HomePod；连接参数由程序自动处理。");
                var devices = new ListBox { MinHeight = 115, MaxHeight = 220, Margin = new(0, 12, 0, 12) };
                foreach (var receiver in _flow.Receivers)
                {
                    var qualified = receiver.PrefersNtpAlac && receiver.Online;
                    devices.Items.Add(new ListBoxItem { Content = receiver.Name + (qualified ? "  ·  可连接" : "  ·  此型号或固件尚待验证"),
                        Tag = receiver, Padding = new(12), IsEnabled = qualified });
                }
                if (devices.Items.Count == 0) Text("暂未发现 HomePod。请确认它已通电、接入同一局域网，并允许同网设备使用 AirPlay，然后重新搜索。");
                _body.Children.Add(devices);
                AsyncAction("使用这只 HomePod", token => devices.SelectedItem is ListBoxItem { Tag: Receiver receiver }
                    ? _flow.ConnectAsync(receiver, token) : ShowSelectionRequired());
                AsyncAction("重新搜索", _flow.RefreshAsync);
                Text("当前实机资格覆盖单只 HomePod mini、27.0 固件；列表不会把未验证的设备标成已支持。", 13);
                break;
            case NativeSetupStep.Configuring:
                Text("正在设置 HomePod 输出名称、测量 Windows 音量并准备音频连接。随后会自动选择输出并播放一次提示音，无需打开声音属性或播放器。");
                break;
            case NativeSetupStep.Testing:
                Text("请留意“" + (_flow.SelectedReceiver?.Name ?? "HomePod") + "”。连接就绪后会自动播放一次短提示音，播放结束后即可确认。");
                break;
            case NativeSetupStep.Listening:
                Text("Windows 输出和名称已自动配置，确认音频已播放完毕。只需确认刚才是否听到，程序会据此保存或检查连接。");
                AsyncAction("听到了", async token => { await _flow.ConfirmMusicAsync(token); if (_flow.Step == NativeSetupStep.Ready) Close(); });
                AsyncAction("没听到", _flow.RejectMusicAsync);
                break;
            case NativeSetupStep.Ready:
                Text("“" + OutputName() + "”已准备好，直接播放音乐或视频即可。需要切换到其他音箱或耳机时，在 Windows 中选择输出。");
                Text("音量、静音和应用混音器都使用 Windows 原生控件。后台会随当前用户登录启动；日常不需要打开本向导。");
                Action("完成", Close);
                break;
            case NativeSetupStep.Blocked:
                Text("为保护已有音频配置，设置暂未继续。请按下面的原因处理后重新检查；不要反复安装或重启。");
                AsyncAction("重新检查", _flow.RefreshAsync);
                Action("打开日志文件夹", () => Open(AppPaths.LogDirectory));
                break;
            default:
                Text("正在核对驱动、现有设备记录和后台状态，请稍候。");
                break;
        }
        Text("音频驱动：VB-CABLE by VB-Audio Software（Donationware）。", 12);
        Action("驱动来源、许可与捐助", () => Open("https://vb-audio.com/Services/licensing.htm"));
        _status.Text = _busy ? "正在处理，请稍候…" : _operationError ?? _flow.Error ?? "";
        _body.Children.Add(_status);
        foreach (var button in _body.Children.OfType<Button>()) button.IsEnabled = !_busy;
    }
    private void FlowChanged() { if (!_closing) _ = Dispatcher.BeginInvoke(() => { _operationError = null; RefreshView(); }); }
    private string OutputName()
    {
        var endpoint = _flow.Inspection?.Endpoint;
        if (!string.IsNullOrWhiteSpace(endpoint?.EndpointLabel)) return endpoint.EndpointLabel;
        if (!string.IsNullOrWhiteSpace(endpoint?.FriendlyName)) return endpoint.FriendlyName;
        return _flow.Inspection?.Binding?.AssignedName ?? (_flow.SelectedReceiver is { } receiver ? NativeSetupFlow.OutputName(receiver) : "HomePod");
    }
    private static Task ShowSelectionRequired() => Task.FromException(new InvalidOperationException("请先选择一只可连接的 HomePod。"));
    private void Text(string text, double size = 15, FontWeight? weight = null)
        => _body.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 9, 0, 6), LineHeight = size * 1.55 });
    private void Action(string title, System.Action action)
    {
        var button = Button(title); button.Click += (_, _) => { try { action(); } catch (Exception error) { _status.Text = DiagnosticText.Protect(error.Message); } };
    }
    private void AsyncAction(string title, Func<CancellationToken, Task> action)
    { var button = Button(title); button.Click += async (_, _) => await RunAsync(action); }
    private Button Button(string title)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180,
            Padding = new(18, 9, 18, 9), Margin = new(0, 6, 0, 2) };
        _body.Children.Add(button); return button;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closing) return;
        _busy = true; _operationError = null;
        foreach (var button in _body.Children.OfType<Button>()) button.IsEnabled = false;
        _status.Text = "正在处理，请稍候…";
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { AppPaths.Log("First-run setup failed: " + error); _operationError = DiagnosticText.Protect(error.Message); }
        finally { _busy = false; if (!_closing) RefreshView(); }
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
