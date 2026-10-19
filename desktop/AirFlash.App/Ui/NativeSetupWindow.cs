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
    public NativeSetupWindow(NativeSetupFlow flow, bool initializeOnLoad = true)
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
        if (initializeOnLoad) Loaded += async (_, _) => await RunAsync(_flow.RefreshAsync);
        RefreshView();
    }
    public void RefreshView()
    {
        _body.Children.Clear();
        Text("将 HomePod 添加为 Windows 输出", 25, FontWeights.SemiBold);
        Text("首次设置完成后，只用 Windows 选择输出、调整音量和静音。", 15);
        Text(_flow.Step switch {
            NativeSetupStep.Driver => "1 · 安装独立音频驱动",
            NativeSetupStep.Restart => "1 · 完成驱动重启",
            NativeSetupStep.Receiver => "2 · 选择 HomePod",
            NativeSetupStep.NameOutput => "2 · 确认 Windows 输出名称",
            NativeSetupStep.Listening => "3 · 用正常音乐确认连接",
            NativeSetupStep.Ready => "已准备好",
            NativeSetupStep.Blocked => "需要先处理一个问题",
            _ => "正在检查这台电脑"
        }, 21, FontWeights.SemiBold);
        switch (_flow.Step)
        {
            case NativeSetupStep.Driver:
                Text("安装官方 VB-CABLE 独立驱动。请在官方页面下载 VBCABLE_Driver_Pack45.zip，解压后以管理员身份运行 x64 安装程序，并按厂商提示重启。应用不会修改其他音频设备或代替你重启。");
                Text("请先保留本向导的安装检查记录；重启后重新打开 AppleHome 即可继续。驱动由厂商单独提供。");
                Action("打开官方驱动页面", () => Open("https://vb-audio.com/Cable/"));
                AsyncAction("已安装，重新检查", _flow.RefreshAsync);
                break;
            case NativeSetupStep.Restart:
                Text("驱动已被发现，但尚未验证安装后的重启。请保存工作并重启 Windows，然后重新打开 AppleHome。无需再次安装驱动。");
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
                AsyncAction("连接并验证", token => devices.SelectedItem is ListBoxItem { Tag: Receiver receiver }
                    ? _flow.ConnectAsync(receiver, token) : ShowSelectionRequired());
                AsyncAction("重新搜索", _flow.RefreshAsync);
                Text("当前实机资格覆盖单只 HomePod mini、27.0 固件；列表不会把未验证的设备标成已支持。", 13);
                break;
            case NativeSetupStep.NameOutput:
                var name = NativeSetupFlow.OutputName(_flow.SelectedReceiver!);
                Text("Windows 要求在系统属性里确认一次设备名称。打开下方声音设置，在“播放”中选择普通 CABLE Input，进入“属性 → 常规”，将顶部名称改为：");
                Text(name, 19, FontWeights.SemiBold);
                Text("请选择普通播放设备，不要选择 16ch 或录音设备。这是一次性系统命名，之后直接在 Windows 输出列表使用该名称。");
                Action("复制输出名称", () => Clipboard.SetText(name));
                Action("打开 Windows 声音设置", OpenSoundProperties);
                AsyncAction("已命名，继续连接", token => _flow.ConnectAsync(_flow.SelectedReceiver!, token));
                break;
            case NativeSetupStep.Listening:
                Text("连接已准备好。请在 Windows 声音输出中选择“" + OutputName() + "”，用你常用的播放器播放正常音乐，并用 Windows 音量调到舒适的位置。");
                Text("让正常音乐连续播放片刻，确认 HomePod 出声且音量可以调整后，点击完成。如果无声或断续，可让程序调稳连接，再继续听同一首音乐；不需要填写参数。");
                AsyncAction("能正常听到音乐，完成设置", _flow.ConfirmMusicAsync);
                AsyncAction("没有声音或有断续，自动调稳", _flow.RejectMusicAsync);
                break;
            case NativeSetupStep.Ready:
                Text("HomePod 输出已准备好。以后在 Windows 声音输出中选择“" + OutputName() + "”，直接播放音乐或视频即可。");
                Text("音量、静音和应用混音器都使用 Windows 原生控件。后台会随当前用户登录启动；日常不需要打开本向导。");
                Action("打开 Windows 声音设置", () => Open("ms-settings:sound"));
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
        _status.Text = _busy ? "正在处理，请稍候…" : _operationError ?? _flow.Error ?? "";
        _body.Children.Add(_status);
        foreach (var button in _body.Children.OfType<Button>()) button.IsEnabled = !_busy;
    }
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
        finally { _lifetime.Dispose(); _allowClose = true; _ = Dispatcher.BeginInvoke(Close); }
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private static void OpenSoundProperties()
    {
        var process = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe")) { UseShellExecute = true };
        process.ArgumentList.Add("mmsys.cpl,,0"); Process.Start(process);
    }
}
