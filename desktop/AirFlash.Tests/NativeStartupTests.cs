using System.Runtime.Versioning;
using System.Xml.Linq;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

[SupportedOSPlatform("windows")]
public sealed class NativeStartupTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Exe = @"C:\Users\Test\AppData\Local\AppleHome\app\AppleHome.exe";
    [Theory]
    [InlineData("")]
    [InlineData("--native-agent")]
    [InlineData("--native-enable")]
    [InlineData("--native-disable")]
    [InlineData("--native-check")]
    public void AgentDispatchPrecedesWpfInitialization(string command)
    {
        Assert.True(NativeStartupPolicy.IsHeadless(command.Length == 0 ? [] : [command]));
        Assert.False(NativeStartupPolicy.IsHeadless(["--legacy-panel"]));
        Assert.False(NativeStartupPolicy.IsHeadless(["--ui-smoke"]));
    }
    [Fact]
    public void OwnedNativeTaskHasBoundedRestartWithoutPasswordOrService()
    {
        var xml = NativeStartupPolicy.TaskXml(Sid, Exe); var root = XDocument.Parse(xml); XNamespace ns = root.Root!.Name.Namespace;
        string Value(string local) => root.Descendants(ns + local).Single().Value;
        Assert.Equal("InteractiveToken", Value("LogonType")); Assert.Equal("LeastPrivilege", Value("RunLevel"));
        Assert.Equal("3", Value("Count")); Assert.Equal("PT1M", Value("Interval"));
        Assert.All(root.Descendants(ns + "UserId"), user => Assert.Equal(Sid, user.Value));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy")); Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries")); Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("--native-agent", Value("Arguments")); Assert.DoesNotContain("Password", xml);
    }
    [Theory]
    [InlineData("sid")]
    [InlineData("path")]
    [InlineData("arguments")]
    [InlineData("description")]
    [InlineData("restart")]
    public void UserChangedStartupTaskIsPreserved(string change)
    {
        var original = NativeStartupPolicy.TaskXml(Sid, Exe);
        var receipt = NativeStartupPolicy.Receipt(original, Sid, Exe);
        Assert.True(NativeStartupPolicy.OwnsTask(original, receipt, Sid, Exe));
        var modified = change switch {
            "sid" => original.Replace(Sid, "S-1-5-21-999"),
            "path" => original.Replace(Exe, @"C:\Other\AppleHome.exe"),
            "arguments" => original.Replace("--native-agent", "--legacy-panel"),
            "description" => original.Replace("AppleHome native output", "User edited task"),
            _ => original.Replace("<Count>3</Count>", "<Count>8</Count>")
        };
        Assert.False(NativeStartupPolicy.OwnsTask(modified, receipt, Sid, Exe));
        Assert.False(NativeStartupPolicy.OwnsTask(original, null, Sid, Exe));
    }
    [Fact]
    public void UserChangedInstalledBinaryCannotBeOverwrittenByEnable()
    {
        var receipt = NativeStartupPolicy.Receipt(NativeStartupPolicy.TaskXml(Sid, Exe), Sid, Exe, new string('a', 64));
        Assert.True(NativeStartupPolicy.OwnsInstalledBinary(receipt, Sid, Exe, new string('a', 64)));
        Assert.False(NativeStartupPolicy.OwnsInstalledBinary(receipt, Sid, Exe, new string('b', 64)));
        Assert.False(NativeStartupPolicy.OwnsInstalledBinary(null, Sid, Exe, new string('a', 64)));
    }
    [Fact]
    public async Task FirstEnableArmsWithoutReloginAndRepeatedEnableKeepsOneAgent()
    {
        var suffix = "native-start-test-" + Guid.NewGuid().ToString("N"); SingleInstance? agent = null; var starts = 0;
        async Task<bool> Ready(CancellationToken cancellation)
        {
            await using var probe = new SingleInstance(suffix);
            return !probe.IsOwner && await probe.QueryReadyAsync(cancellation);
        }
        Task Start()
        {
            agent = new(suffix); Assert.True(agent.IsOwner); agent.Listen(_ => { }, () => true); starts++; return Task.CompletedTask;
        }
        try
        {
            await NativeStartupPolicy.EnsureRunningAsync(Ready, Start, default);
            await NativeStartupPolicy.EnsureRunningAsync(Ready, Start, default);
            Assert.Equal(1, starts);
        }
        finally { if (agent is not null) await agent.DisposeAsync(); }
    }
    [Fact]
    public async Task SingleInstanceCanDisposeAfterThreadSwitchAndReplyToReadyAndStop()
    {
        var suffix = "native-pipe-test-" + Guid.NewGuid().ToString("N");
        var owner = new SingleInstance(suffix); Assert.True(owner.IsOwner);
        var command = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Listen(value => command.TrySetResult(value), () => true);
        await using (var client = new SingleInstance(suffix))
        {
            Assert.False(client.IsOwner); Assert.True(await client.QueryReadyAsync(default));
            await client.NotifyExistingAsync(2); Assert.Equal(2, await command.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        await Task.Run(async () => await owner.DisposeAsync());
        await using var next = new SingleInstance(suffix); Assert.True(next.IsOwner);
    }
    [Fact]
    public async Task DisconnectedReadyClientDoesNotEndTheAgentPipeListener()
    {
        var suffix = "native-disconnect-test-" + Guid.NewGuid().ToString("N");
        await using var owner = new SingleInstance(suffix);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var calls = 0;
        owner.Listen(_ => { }, () => { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); } return true; });
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var pipeName = $"{AppPaths.Name}.Wpf.{identity.User!.Value}.{suffix}";
        await using (var abandoned = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await abandoned.ConnectAsync(2000); await abandoned.WriteAsync(new byte[] { 3 }); await abandoned.FlushAsync();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        }
        release.Set();
        await using var next = new SingleInstance(suffix); Assert.True(await next.QueryReadyAsync(default));
    }
    [Fact]
    public async Task FatalHostReturnsNonzeroWhileExplicitCancellationReturnsZero()
    {
        var errors = 0;
        var failed = await NativeStartupPolicy.RunHeadlessAsync(_ => Task.FromException(new IOException("fatal host")), _ => { errors++; return Task.CompletedTask; }, default);
        Assert.Equal(1, failed); Assert.Equal(1, errors);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await NativeStartupPolicy.RunHeadlessAsync(token => Task.FromCanceled(token), _ => throw new InvalidOperationException("No failure notification on explicit stop"), stop.Token);
        Assert.Equal(0, cancelled);
    }
    [Fact]
    public void LegacyRunRemovalRequiresExactKnownPathAndArguments()
    {
        Assert.True(NativeStartupPolicy.OwnsLegacyRun($"\"{Exe}\" --startup", Exe));
        Assert.False(NativeStartupPolicy.OwnsLegacyRun($"\"{Exe}\" --other", Exe));
        Assert.False(NativeStartupPolicy.OwnsLegacyRun("\"C:\\Other\\app.exe\" --startup", Exe));
    }
    [Fact]
    public void BootBeforePreinstallProofCannotQualifyDriverRestart()
    {
        var installedAfter = new DateTimeOffset(2026,10,15,0,0,0,TimeSpan.Zero);
        Assert.False(NativeStartupPolicy.RebootedAfter(installedAfter, installedAfter - TimeSpan.FromDays(1)));
        Assert.True(NativeStartupPolicy.RebootedAfter(installedAfter, installedAfter + TimeSpan.FromMinutes(1)));
    }
}
