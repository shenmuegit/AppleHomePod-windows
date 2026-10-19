using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeSetupFlowTests
{
    private static EndpointIdentity Endpoint => new("endpoint-1", @"ROOT\MEDIA\0009", NativeBindingPolicy.HardwareId,
        NativeBindingPolicy.Provider, NativeBindingPolicy.DriverVersion, NativeBindingPolicy.DriverSha256,
        "CABLE Input", "CABLE Input (VB-Audio Virtual Cable)", "48000/2/32/3", 2, AudioEndpointKind.Playback, true,
        "HomePod – da");
    private static Receiver Mini { get; } = new("9efd9099abd6", "da", "192.0.2.10")
        { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
    private static NativeBinding Binding(int? verifiedBufferMs = null) => new(1, Mini.Id, Endpoint,
        Endpoint.FriendlyName, "HomePod – da", new(false, Endpoint.MixFormat, Endpoint.DriverSha256, 0, 54), verifiedBufferMs);

    [Theory]
    [InlineData(false, false, NativeSetupStep.Driver)]
    [InlineData(true, false, NativeSetupStep.Restart)]
    public async Task DriverAndRestartGatesPreventDiscoveryAndPreview(bool driver, bool restarted, NativeSetupStep expected)
    {
        var platform = new Fake { Inspection = new(driver, restarted, Endpoint, null, false) };
        await using var flow = new NativeSetupFlow(platform);
        await flow.RefreshAsync(default);
        await flow.ConnectAsync(Mini, default);
        Assert.Equal(expected, flow.Step);
        Assert.Equal(["inspect", "inspect"], platform.Calls);
    }

    [Theory]
    [InlineData("Preinstall proof is missing; restore the verified installation record.")]
    [InlineData("An unowned startup task exists; resolve it before continuing.")]
    public async Task ExplicitInstallationBlockTakesPriorityOverUnverifiedRestart(string reason)
    {
        var platform = new Fake { Inspection = new(true, false, Endpoint, null, false, reason) };
        await using var flow = new NativeSetupFlow(platform);
        await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.Equal(reason, flow.Error);
        Assert.Equal(["inspect", "inspect"], platform.Calls);
    }

    [Fact]
    public async Task FreshInstallationDiscoversReceiversWithoutInventingBufferOrCalibration()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform);
        await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step);
        Assert.Equal([Mini], flow.Receivers);
        Assert.Null(flow.SelectedReceiver); Assert.Null(flow.Inspection!.Binding);
        Assert.Equal(["inspect", "discover"], platform.Calls);
    }

    [Theory]
    [InlineData(100)]
    public async Task ExistingQualifiedBindingIsReadyWithoutRediscoveryOrBufferChanges(int? verifiedBufferMs)
    {
        var binding = Binding(verifiedBufferMs);
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, true) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Ready, flow.Step);
        Assert.Same(binding, flow.Inspection!.Binding);
        Assert.Equal(verifiedBufferMs, flow.Inspection.Binding!.VerifiedBufferMs);
        Assert.Equal(["inspect"], platform.Calls);
    }

    [Fact]
    public async Task ReadyAgentWithoutReferenceStillRequiresNormalMusicConfirmation()
    {
        var platform = new Fake { Inspection = new(true, true, Endpoint, Binding(), true) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step); Assert.Equal(["inspect", "discover"], platform.Calls);
        await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.DoesNotContain("complete", platform.Calls);
        await flow.ConfirmMusicAsync(default); Assert.Equal(NativeSetupStep.Ready, flow.Step);
    }

    [Fact]
    public async Task TaggedReferenceIsReadyOnlyAfterItsCurrentReceiverProfileMatches()
    {
        var binding = Binding(100) with { VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Mini, 44100) };
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, true) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Ready, flow.Step); Assert.Equal(["inspect", "discover"], platform.Calls);
        Assert.Same(binding, flow.Inspection!.Binding);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("group")]
    [InlineData("rate")]
    public async Task StaleTaggedReferenceCannotBypassCurrentProfileDiscovery(string changed)
    {
        var binding = Binding(100) with { VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Mini, changed == "rate" ? 48000 : 44100) };
        var receiver = changed switch { "firmware" => Mini with { SoftwareVersion = "26.0" }, "group" => Mini with { GroupId = "tv" }, _ => Mini };
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, true), Receivers = [receiver] };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step); Assert.Equal(["inspect", "discover"], platform.Calls);
        Assert.Same(binding, flow.Inspection!.Binding); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("recording")]
    [InlineData("driver")]
    [InlineData("blocked")]
    public async Task UnverifiedOrBlockedEndpointCannotReachMusic(string problem)
    {
        var endpoint = problem switch { "missing" => null, "recording" => Endpoint with { Kind = AudioEndpointKind.Recording },
            "driver" => Endpoint with { DriverSha256 = new string('a', 64) }, _ => Endpoint };
        var platform = new Fake { Inspection = new(true, true, endpoint, null, false, problem == "blocked" ? "Existing cable is in use." : null) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.False(string.IsNullOrWhiteSpace(flow.Error));
        Assert.Equal(["inspect"], platform.Calls);
    }

    [Fact]
    public async Task RawOutputRequiresWindowsNamingBeforePreviewAndRetainsSelection()
    {
        var raw = Endpoint with { EndpointLabel = "CABLE Input" };
        var platform = new Fake { Inspection = new(true, true, raw, null, false) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.NameOutput, flow.Step); Assert.Equal(Mini, flow.SelectedReceiver);
        Assert.DoesNotContain("preview", platform.Calls);
        platform.Inspection = platform.Inspection with { Endpoint = Endpoint };
        await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step);
        Assert.Equal(1, platform.Calls.Count(call => call == "preview"));
    }

    [Fact]
    public async Task UserNamedOutputIsKeptWithoutForcingSuggestedName()
    {
        var endpoint = Endpoint with { EndpointLabel = "客厅音乐" };
        var platform = new Fake { Inspection = new(true, true, endpoint, null, false) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.Equal("客厅音乐", flow.Inspection!.Endpoint!.EndpointLabel);
    }

    [Fact]
    public async Task DesiredFriendlyNameWithoutSeparateLabelSatisfiesExistingNamingRule()
    {
        var endpoint = Endpoint with { EndpointLabel = null, FriendlyName = "HomePod – da" };
        var platform = new Fake { Inspection = new(true, true, endpoint, null, false) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step);
    }

    [Theory]
    [InlineData("undiscovered")]
    [InlineData("endpoint-id")]
    [InlineData("offline")]
    [InlineData("unqualified")]
    public async Task OnlyDiscoveredQualifiedStableReceiverCanStart(string problem)
    {
        var receiver = problem switch {
            "undiscovered" => Mini with { Id = "001122334455" }, "endpoint-id" => Mini with { Id = "192.0.2.10:7000" },
            "offline" => Mini with { Online = false }, _ => Mini with { SoftwareVersion = "26.0" }
        };
        var platform = new Fake { Receivers = problem == "undiscovered" ? [Mini] : [receiver] };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(receiver, default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.NotNull(flow.Error); Assert.Null(platform.PreviewReceiver);
    }

    [Fact]
    public async Task DiscoveredReceiverCannotBeReplacedWithForgedTransportOrProfile()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        await flow.ConnectAsync(Mini with { Address = "203.0.113.99", Name = "forged", SoftwareVersion = "1" }, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.Equal(Mini, flow.SelectedReceiver);
        Assert.Equal("192.0.2.10", platform.PreviewReceiver!.Address);
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("endpoint")]
    [InlineData("invalid-calibration")]
    public async Task ExistingBindingCannotBeSilentlyReplaced(string change)
    {
        var binding = change switch { "receiver" => Binding() with { ReceiverId = "001122334455" },
            "endpoint" => Binding() with { Endpoint = Endpoint with { EndpointId = "other" } },
            _ => Binding() with { Calibration = Binding().Calibration! with { DriverSha256 = new string('b', 64) } } };
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, false) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.NotNull(flow.Error); Assert.Null(platform.PreviewReceiver);
    }

    [Fact]
    public async Task PreviewRechecksDriverGateInsteadOfUsingStaleInspection()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        platform.Inspection = platform.Inspection with { RebootVerified = false };
        await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Restart, flow.Step); Assert.Null(platform.PreviewReceiver);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(100)]
    public async Task OnlyExplicitMusicConfirmationCompletesWithoutChangingBufferRecord(int? verifiedBufferMs)
    {
        var binding = Binding(verifiedBufferMs);
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, false) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.DoesNotContain("complete", platform.Calls);
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Ready, flow.Step); Assert.Same(binding, platform.Inspection.Binding);
        Assert.Equal(verifiedBufferMs, platform.Inspection.Binding!.VerifiedBufferMs);
        Assert.True(platform.Calls.IndexOf("preview") < platform.Calls.IndexOf("complete"));
    }

    [Fact]
    public async Task ConfirmationBeforePreviewCannotEnableOutput()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task RejectedMusicStopsPreviewWithoutCompleting()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        await flow.RejectMusicAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step); Assert.Contains("stop", platform.Calls);
        Assert.DoesNotContain("complete", platform.Calls); Assert.Equal(Mini, flow.SelectedReceiver);
    }

    [Fact]
    public async Task ExplicitNegativeMusicFeedbackKeepsPreviewOnlyWhenAdaptationStarted()
    {
        var binding = Binding();
        var platform = new Fake { Inspection = new(true, true, Endpoint, binding, false), Adapt = _ => Task.FromResult(true) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        platform.Calls.Clear();
        await flow.RejectMusicAsync(default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.Equal(["adapt"], platform.Calls);
        Assert.Same(binding, platform.Inspection.Binding); Assert.Null(binding.VerifiedBufferMs);
        platform.Adapt = _ => Task.FromResult(false);
        await flow.RejectMusicAsync(default);
        Assert.Equal(NativeSetupStep.Receiver, flow.Step); Assert.Equal(["adapt", "adapt", "stop"], platform.Calls);
        Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task EvidenceStillPendingKeepsNormalMusicPreviewAndCanBeConfirmedLater()
    {
        var platform = new Fake { Complete = _ => Task.FromException(new NativeSetupPendingException("请继续播放正常音乐，片刻后再确认。")) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Listening, flow.Step); Assert.Contains("正常音乐", flow.Error);
        Assert.DoesNotContain("stop", platform.Calls); Assert.Null(platform.Inspection.Binding);
        platform.Complete = null;
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Ready, flow.Step); Assert.Null(flow.Error);
        Assert.Equal(2, platform.Calls.Count(call => call == "complete"));
    }

    [Fact]
    public async Task RefreshStopsUnconfirmedPreviewBeforeInspectingAgain()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        platform.Calls.Clear(); await flow.RefreshAsync(default);
        Assert.Equal(["stop", "inspect", "discover"], platform.Calls); Assert.Equal(NativeSetupStep.Receiver, flow.Step);
    }

    [Fact]
    public async Task DuplicateConnectAndEarlyConfirmCannotStartOrCompleteTwice()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Fake { Preview = _ => { entered.SetResult(); return release.Task; } };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        var connecting = flow.ConnectAsync(Mini, default); await entered.Task;
        await flow.ConnectAsync(Mini, default); await flow.ConfirmMusicAsync(default);
        Assert.Equal(1, platform.Calls.Count(call => call == "preview")); Assert.DoesNotContain("complete", platform.Calls);
        release.SetResult(); await connecting; Assert.Equal(NativeSetupStep.Listening, flow.Step);
    }

    [Fact]
    public async Task DuplicateConfirmationCompletesOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Fake { Complete = _ => { entered.SetResult(); return release.Task; } };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        var completing = flow.ConfirmMusicAsync(default); await entered.Task;
        await flow.ConfirmMusicAsync(default); release.SetResult(); await completing;
        Assert.Equal(1, platform.Calls.Count(call => call == "complete")); Assert.Equal(NativeSetupStep.Ready, flow.Step);
    }

    [Fact]
    public async Task CancellationAfterPreviewStartedStopsItAndCannotPublishListeningOrReady()
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Fake { Preview = _ => { entered.SetResult(); return release.Task; } };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default);
        var connecting = flow.ConnectAsync(Mini, cancellation.Token); await entered.Task;
        cancellation.Cancel(); release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.NotEqual(NativeSetupStep.Listening, flow.Step); Assert.NotEqual(NativeSetupStep.Ready, flow.Step);
        Assert.Contains("stop", platform.Calls); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task EndpointChangeDuringMusicCannotBeConfirmed()
    {
        var platform = new Fake(); await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        platform.Inspection = platform.Inspection with { Endpoint = Endpoint with { MixFormat = "44100/2/32/3" } };
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.NotNull(flow.Error);
        Assert.Contains("stop", platform.Calls); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task FailedPreviewIsStoppedAndExposesActionableError()
    {
        var platform = new Fake { Preview = _ => Task.FromException(new IOException("HomePod is busy; retry after releasing iPhone playback.")) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.Contains("retry", flow.Error);
        Assert.Contains("stop", platform.Calls); Assert.DoesNotContain("complete", platform.Calls);
    }

    [Fact]
    public async Task FailedCompletionCannotPublishReady()
    {
        var platform = new Fake { Complete = _ => Task.FromException(new IOException("Could not register startup task; retry.")) };
        await using var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        await flow.ConfirmMusicAsync(default);
        Assert.Equal(NativeSetupStep.Blocked, flow.Step); Assert.Contains("retry", flow.Error); Assert.Contains("stop", platform.Calls);
    }

    [Fact]
    public async Task DisposingDuringPreviewCancelsAndStopsBeforeDisposingPlatform()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Fake { Preview = token => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); } };
        var flow = new NativeSetupFlow(platform); await flow.RefreshAsync(default); var connecting = flow.ConnectAsync(Mini, default); await entered.Task;
        await flow.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal("dispose", platform.Calls[^1]); Assert.True(platform.Calls.IndexOf("stop") < platform.Calls.IndexOf("dispose"));
        Assert.NotEqual(NativeSetupStep.Listening, flow.Step); Assert.NotEqual(NativeSetupStep.Ready, flow.Step);
    }

    [Fact]
    public void SuggestedOutputNameRemovesControlsAndFitsWindowsBindingLimit()
    {
        Assert.Equal("HomePod – 客厅da", NativeSetupFlow.OutputName(Mini with { Name = "  客厅\r\nda\t  " }));
        Assert.Equal("HomePod", NativeSetupFlow.OutputName(Mini with { Name = " \r\n " }));
        var name = NativeSetupFlow.OutputName(Mini with { Name = new string('a', 245) + "🎵" });
        Assert.InRange(name.Length, 1, 256); Assert.False(char.IsHighSurrogate(name[^1])); Assert.DoesNotContain(name, char.IsControl);
    }

    private sealed class Fake : INativeSetupPlatform
    {
        public NativeSetupInspection Inspection = new(true, true, Endpoint, null, false);
        public IReadOnlyList<Receiver> Receivers = [Mini];
        public readonly List<string> Calls = [];
        public Receiver? PreviewReceiver;
        public Func<CancellationToken, Task>? Preview, Complete;
        public Func<CancellationToken, Task<bool>>? Adapt;
        public Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation) { Calls.Add("inspect"); return Task.FromResult(Inspection); }
        public Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation) { Calls.Add("discover"); return Task.FromResult(Receivers); }
        public Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation) { Calls.Add("preview"); PreviewReceiver = receiver; return Preview?.Invoke(cancellation) ?? Task.CompletedTask; }
        public Task CompleteAsync(CancellationToken cancellation) { Calls.Add("complete"); return Complete?.Invoke(cancellation) ?? Task.CompletedTask; }
        public Task<bool> TryAdaptMusicAsync(CancellationToken cancellation) { Calls.Add("adapt"); return Adapt?.Invoke(cancellation) ?? Task.FromResult(false); }
        public Task StopPreviewAsync(CancellationToken cancellation) { Calls.Add("stop"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
