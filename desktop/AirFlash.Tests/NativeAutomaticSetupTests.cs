using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeAutomaticSetupTests
{
    [Fact]
    public async Task SingleHomePodAutomaticallyConfiguresAndPlaysBeforeAskingOnlyForHearing()
    {
        var platform = new Fake(); await using var setup = new NativeSetupFlow(platform);
        var steps = new List<NativeSetupStep>(); setup.Changed += () => steps.Add(setup.Step);
        await setup.StartAutomaticAsync(default);
        Assert.Equal(NativeSetupStep.Listening, setup.Step);
        Assert.Equal(["inspect", "discover", "inspect", "prepare", "preview", "play"], platform.Calls);
        Assert.Contains(NativeSetupStep.Testing, steps); Assert.DoesNotContain("complete", platform.Calls);
        await setup.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Ready, setup.Step); Assert.Equal("complete", platform.Calls[^1]);
    }

    [Fact]
    public async Task MissingDriverIsInstalledAndReinspectedButNeverBypassesReboot()
    {
        var platform = new Fake { Driver = false, Reboot = false }; await using var setup = new NativeSetupFlow(platform);
        await setup.StartAutomaticAsync(default);
        Assert.Equal(["inspect", "install", "inspect"], platform.Calls);
        Assert.Equal(NativeSetupStep.Restart, setup.Step); Assert.DoesNotContain("play", platform.Calls);
    }

    [Fact]
    public async Task TwoHomePodsRequireDeviceChoiceAndNeverGuess()
    {
        var platform = new Fake { Multiple = true }; await using var setup = new NativeSetupFlow(platform);
        await setup.StartAutomaticAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, setup.Step); Assert.DoesNotContain("prepare", platform.Calls);
    }

    [Fact]
    public async Task AutomaticChoiceWaitsForDiscoveryWindowRatherThanFirstResolver()
    {
        var browse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Fake { Browse = browse.Task }; await using var setup = new NativeSetupFlow(platform);
        var checking = setup.StartAutomaticAsync(default);
        Assert.Contains("discover", platform.Calls); Assert.DoesNotContain("prepare", platform.Calls);
        platform.Multiple = true; browse.SetResult(); await checking;
        Assert.Equal(NativeSetupStep.Receiver, setup.Step); Assert.DoesNotContain("play", platform.Calls);
    }

    [Fact]
    public async Task FailedAutomaticConfigurationIsRolledBackWithoutRequestingManualNaming()
    {
        var platform = new Fake { FailPrepare = true }; await using var setup = new NativeSetupFlow(platform);
        await setup.StartAutomaticAsync(default);
        Assert.Equal(NativeSetupStep.Blocked, setup.Step); Assert.Contains("stop", platform.Calls);
        Assert.DoesNotContain("play", platform.Calls); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task NegativeAnswerAdaptsAndReplaysAutomaticallyWithinBound()
    {
        var platform = new Fake(); await using var setup = new NativeSetupFlow(platform);
        await setup.StartAutomaticAsync(default); platform.Calls.Clear();
        await setup.RejectMusicAsync(default);
        Assert.Equal(["adapt", "play"], platform.Calls); Assert.Equal(NativeSetupStep.Listening, setup.Step);
        platform.CanAdapt = false; await setup.RejectMusicAsync(default);
        Assert.Equal(NativeSetupStep.Blocked, setup.Step); Assert.Contains("stop", platform.Calls);
        Assert.DoesNotContain("complete", platform.Calls);
    }

    private sealed class Fake : INativeSetupPlatform
    {
        public bool Driver = true, Reboot = true, Multiple, FailPrepare, CanAdapt = true;
        public Task? Browse;
        public readonly List<string> Calls = [];
        private readonly Receiver _receiver = new("9efd9099abd6", "da", "192.0.2.10")
            { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
        private readonly EndpointIdentity _endpoint = new("endpoint", "adapter", NativeBindingPolicy.HardwareId,
            NativeBindingPolicy.Provider, NativeBindingPolicy.DriverVersion, NativeBindingPolicy.DriverSha256,
            "CABLE Input", "CABLE Input", "48000/2/32/3", 2, AudioEndpointKind.Playback, true, "CABLE Input");
        public Task<NativeSetupInspection> InspectAsync(CancellationToken token)
        { Calls.Add("inspect"); return Task.FromResult(new NativeSetupInspection(Driver, Reboot, _endpoint, null, false)); }
        public Task<NativeDriverInstallOutcome> InstallDriverAsync(CancellationToken token) { Calls.Add("install"); Driver = true; return Task.FromResult(NativeDriverInstallOutcome.VerificationRequired); }
        public async Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken token)
        { Calls.Add("discover"); if (Browse is not null) await Browse.WaitAsync(token); return Multiple ? [_receiver, _receiver with { Id = "001122334455" }] : [_receiver]; }
        public Task PrepareOutputAsync(Receiver receiver, CancellationToken token)
        { Calls.Add("prepare"); return FailPrepare ? Task.FromException(new IOException("name denied")) : Task.CompletedTask; }
        public Task StartPreviewAsync(Receiver receiver, CancellationToken token) { Calls.Add("preview"); return Task.CompletedTask; }
        public Task PlayConfirmationAsync(CancellationToken token) { Calls.Add("play"); return Task.CompletedTask; }
        public Task<bool> TryAdaptMusicAsync(CancellationToken token) { Calls.Add("adapt"); return Task.FromResult(CanAdapt); }
        public Task CompleteAsync(CancellationToken token) { Calls.Add("complete"); return Task.CompletedTask; }
        public Task StopPreviewAsync(CancellationToken token) { Calls.Add("stop"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
