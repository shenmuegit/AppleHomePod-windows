using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using static AirFlash.App.Services.ShellNotification;
namespace AirFlash.App.Services;

internal static class NativeNotification
{
    internal static Task ShowAsync(string message, CancellationToken cancellation)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var source = new HwndSource(new HwndSourceParameters("AppleHome.Notification") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
                using var icon = AppIconService.Create(32);
                var guid = Guid.NewGuid();
                bool Notify(TrayOperation operation, TrayIdentity identity)
                {
                    var data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1, Callback = 0x8001,
                        Flags = 1 | 2 | 4 | (identity == TrayIdentity.Guid ? 0x20u : 0), Icon = icon.DangerousGetHandle(), Tip = AppPaths.Name,
                        Guid = identity == TrayIdentity.Guid ? guid : Guid.Empty, Info = "", InfoTitle = "", Version = operation == TrayOperation.SetVersion ? 4u : 0 };
                    return Shell_NotifyIcon((uint)operation, ref data);
                }
                using var registration = new TrayRegistration(Notify, AppPaths.Log);
                if (!registration.TryRegister()) { AppPaths.Log("Native error notification could not register."); return; }
                source.AddHook((IntPtr window, int code, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (code == 0x8001)
                    {
                        var action = registration.UsesVersion4 ? lParam.ToInt64() & 0xffff : lParam.ToInt64();
                        if (action is 0x403 or 0x404 or 0x405) dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                        handled = true;
                    }
                    return IntPtr.Zero;
                });
                var notification = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1,
                    Flags = 0x10 | 0x40 | (registration.Identity == TrayIdentity.Guid ? 0x20u : 0), Guid = registration.Identity == TrayIdentity.Guid ? guid : Guid.Empty,
                    Tip = "", Info = message[..Math.Min(message.Length, 255)], InfoTitle = AppPaths.Name, InfoFlags = 2 | 0x10 | 0x80 };
                if (!Shell_NotifyIcon(1, ref notification)) { AppPaths.Log("Native error notification was not accepted by Windows."); return; }
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                timeout.Tick += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); timeout.Start();
                using var stop = cancellation.Register(() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
                Dispatcher.Run(); timeout.Stop();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { AppPaths.Log($"Native notification failed: {error}"); }
            finally { complete.TrySetResult(); }
        }) { IsBackground = true, Name = "AppleHome error notification" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return complete.Task;
    }
}
