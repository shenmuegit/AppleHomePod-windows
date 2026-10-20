using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Text.Json;
using AirFlash.Core;
namespace AirFlash.App.Services;

[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    private const int MaxStatusBytes = 8192;
    private static readonly JsonSerializerOptions StatusJson = new() { MaxDepth = 8, RespectRequiredConstructorParameters = true };
    public bool IsOwner { get; }
    public SingleInstance(string? isolatedSuffix = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var suffix = identity.User?.Value ?? Environment.UserName;
        _pipeName = AppPaths.Name + ".Wpf." + suffix + (isolatedSuffix is null ? "" : "." + isolatedSuffix);
        _mutex = new(false, @"Local\" + _pipeName, out var created);
        IsOwner = created;
    }
    public Task NotifyExistingAsync() => NotifyExistingAsync(1);
    public async Task NotifyExistingAsync(byte command)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000); await pipe.WriteAsync(new byte[] { command }); await pipe.FlushAsync();
    }
    public async Task<bool> QueryReadyAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token); await pipe.WriteAsync(new byte[] { 3 }, timeout.Token); await pipe.FlushAsync(timeout.Token);
            var reply = new byte[1]; return await pipe.ReadAsync(reply, timeout.Token) == 1 && reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }
    public async Task<NativeAgentStatus?> QueryStatusAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token); await pipe.WriteAsync(new byte[] { 4 }, timeout.Token); await pipe.FlushAsync(timeout.Token);
            var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaxStatusBytes) return null;
            var payload = new byte[length]; await pipe.ReadExactlyAsync(payload, timeout.Token);
            var status = JsonSerializer.Deserialize<NativeAgentStatus>(payload, StatusJson);
            return status?.Validate() is null ? status : null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or JsonException) { return null; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
    }
    public void Listen(Action activate) => Listen(_ => activate());
    public void Listen(Action<byte> command, Func<bool>? isReady = null, Func<NativeAgentStatus?>? status = null) => _server = Task.Run(async () =>
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); request.CancelAfter(TimeSpan.FromSeconds(3));
                    var input = new byte[1];
                    if (await pipe.ReadAsync(input, request.Token) == 1)
                    {
                        if (input[0] == 3) { await pipe.WriteAsync(new byte[] { isReady?.Invoke() == true ? (byte)1 : (byte)0 }, request.Token); await pipe.FlushAsync(request.Token); }
                        else if (input[0] == 4)
                        {
                            var current = status?.Invoke();
                            if (current?.Error is { } error) current = current with { Error = DiagnosticText.Protect(error) };
                            var payload = current is not null && current.Validate() is null ? JsonSerializer.SerializeToUtf8Bytes(current, StatusJson) : [];
                            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length <= MaxStatusBytes ? payload.Length : 0);
                            await pipe.WriteAsync(header, request.Token);
                            if (payload.Length is > 0 and <= MaxStatusBytes) await pipe.WriteAsync(payload, request.Token);
                            await pipe.FlushAsync(request.Token);
                        }
                        else command(input[0]);
                    }
                }
                catch (IOException error) { AppPaths.Log($"Instance client disconnected: {error.Message}"); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { AppPaths.Log("Instance client request timed out."); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException error) { AppPaths.Log(error.ToString()); }
    });
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); if (_server is not null) await _server;
        _mutex.Dispose(); _stop.Dispose();
    }
}
