using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeMusicConfirmationTests
{
    private static Receiver Mini => new("aabbccddeeff", "Living room", "192.0.2.12")
        { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
    private static NativeBinding Binding => NativeEngineHostTests.Binding with { ReceiverId = Mini.Id };
    private static LatencyNegotiation Latency(int applied = 100) => new("192.0.2.12", "apply", null,
        applied, applied, null, null, applied, applied, "negotiated_request", null, 70, 44100);
    private static TransportMetrics Transport(long nonzero = 10, long sendErrors = 0, long syncErrors = 0, long feedbackErrors = 0)
        => TransportMetrics.Parse(JsonSerializer.SerializeToElement(new {
            session_uptime_ms = 6000, sender_late_recoveries = 0, skipped_packets = 0, max_send_lateness_us = 20,
            members = new[] { new { host = "192.0.2.12", media = new { packets_sent = 100, nonzero_pcm_packets_sent = nonzero,
                media_send_errors = sendErrors, sync_send_errors = syncErrors, retransmit_send_errors = 0,
                retransmit_requests = 0, retransmits_sent = 0, retransmit_missing = 0, retransmit_expired = 0, retransmit_queue_drops = 0 },
                health = new { feedback_failures = feedbackErrors, feedback_delayed = false } } }
        }));
    private static SessionSnapshot Playing(int applied = 100) => new(PlaybackState.Streaming, Mini,
        Diagnostics: new(Transport: Transport(), Latency: Latency(applied)), StreamRate: 44100);

    [Fact]
    public void NormalMusicEvidenceReturnsOnlyTheActuallyAppliedValueWithoutWritingAReference()
    {
        var binding = Binding;
        Assert.Equal(200, NativeMusicConfirmation.ConfirmableAppliedMs(binding, Mini, Playing(200), 44100));
        Assert.Null(binding.VerifiedBufferMs);
    }

    [Theory]
    [InlineData("not-streaming")]
    [InlineData("too-early")]
    [InlineData("silent-pcm")]
    [InlineData("no-metrics")]
    [InlineData("not-applied")]
    [InlineData("send-error")]
    [InlineData("sync-error")]
    [InlineData("feedback-error")]
    [InlineData("retransmit-error")]
    [InlineData("missing-receiver")]
    [InlineData("unknown-rate")]
    [InlineData("unknown-host")]
    [InlineData("unknown-member-host")]
    [InlineData("feedback-delayed")]
    public void IncompleteOrUnhealthyTransportCannotBecomeAConfirmedReference(string condition)
    {
        var snapshot = Playing(); var diagnostics = snapshot.Diagnostics!; var transport = diagnostics.Transport!;
        snapshot = condition switch {
            "not-streaming" => snapshot with { State = PlaybackState.Connecting },
            "too-early" => snapshot with { Diagnostics = diagnostics with { Transport = transport with { SessionUptimeMs = 4999 } } },
            "silent-pcm" => snapshot with { Diagnostics = diagnostics with { Transport = Transport(nonzero: 0) } },
            "no-metrics" => snapshot with { Diagnostics = diagnostics with { Transport = null } },
            "not-applied" => snapshot with { Diagnostics = diagnostics with { Latency = Latency() with { AppliedMs = null } } },
            "send-error" => snapshot with { Diagnostics = diagnostics with { Transport = Transport(sendErrors: 1) } },
            "sync-error" => snapshot with { Diagnostics = diagnostics with { Transport = Transport(syncErrors: 1) } },
            "feedback-error" => snapshot with { Diagnostics = diagnostics with { Transport = Transport(feedbackErrors: 1) } },
            "missing-receiver" => snapshot with { Receiver = null },
            "unknown-rate" => snapshot with { StreamRate = 0 },
            "unknown-host" => snapshot with { Diagnostics = diagnostics with { Latency = Latency() with { Host = "" } } },
            "unknown-member-host" => snapshot with { Diagnostics = diagnostics with { Transport = transport with {
                Members = [transport.Members[0] with { Host = "—" }] } } },
            "feedback-delayed" => snapshot with { Diagnostics = diagnostics with { Transport = transport with {
                Members = [transport.Members[0] with { FeedbackDelayed = true }] } } },
            _ => snapshot with { Diagnostics = diagnostics with { Transport = transport with { Members = [transport.Members[0] with { RetransmitSendErrors = 1 }] } } }
        };
        Assert.Throws<NativeSetupPendingException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, snapshot, 44100));
        Assert.Null(Binding.VerifiedBufferMs);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("session")]
    [InlineData("host")]
    [InlineData("rate")]
    [InlineData("profile")]
    public void EvidenceFromAnotherReceiverHostRateOrProfileIsRejected(string changed)
    {
        var binding = changed == "binding" ? Binding with { ReceiverId = "001122334455" } : Binding;
        var snapshot = Playing();
        snapshot = changed switch {
            "session" => snapshot with { Receiver = Mini with { Id = "001122334455" } },
            "host" => snapshot with { Diagnostics = snapshot.Diagnostics! with { Transport = snapshot.Diagnostics!.Transport! with {
                Members = [snapshot.Diagnostics.Transport!.Members[0] with { Host = "192.0.2.99" }] } } },
            "rate" => snapshot with { StreamRate = 48000 },
            "profile" => snapshot with { Receiver = Mini with { SoftwareVersion = "26.0" } },
            _ => snapshot
        };
        Assert.Throws<InvalidOperationException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(binding, Mini, snapshot, 44100));
    }

    [Theory]
    [InlineData(100, 0, 200)]
    [InlineData(200, 1, 400)]
    [InlineData(400, 2, null)]
    [InlineData(250, 0, 500)]
    [InlineData(500, 1, 1000)]
    [InlineData(2000, 0, null)]
    public void ExplicitRecoveryUsesTheAppliedValueAndStopsAfterTwoAttempts(int applied, int attempts, int? next)
    {
        var binding = Binding;
        Assert.Equal(next, NativeMusicConfirmation.RecoveryCandidateMs(binding, Mini, Playing(applied), attempts, 44100));
        Assert.Null(binding.VerifiedBufferMs);
    }

    [Fact]
    public void KnownReferenceOrReceiverBoundsCannotBeOverriddenByNoSoundRecovery()
    {
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding with { VerifiedBufferMs = 100 }, Mini, Playing(), 0, 44100));
        var playing = Playing();
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding, Mini,
            playing with { Diagnostics = playing.Diagnostics! with { Latency = Latency() with { ReceiverMinSamples = 4410 } } }, 0, 44100));
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding, Mini,
            playing with { Diagnostics = playing.Diagnostics! with { Latency = Latency() with { ReceiverMaxSamples = 44100 } } }, 0, 44100));
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding, Mini,
            playing with { Receiver = Mini with { Id = "001122334455" } }, 0, 44100));
    }

    [Fact]
    public void RecoveryCannotUseAnAppliedValueWithoutTheCurrentReceiverAndRate()
    {
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding, Mini, Playing() with { Receiver = null }, 0, 44100));
        Assert.Null(NativeMusicConfirmation.RecoveryCandidateMs(Binding, Mini, Playing() with { StreamRate = 0 }, 0, 44100));
    }

    [Fact]
    public void RecoveredFeedbackFailureCanConfirmAtTheSameCandidateAfterARepeatedCleanWindow()
    {
        var baseline = Playing() with { Diagnostics = new(Transport: Transport(feedbackErrors: 1), Latency: Latency()) };
        Assert.Throws<NativeSetupPendingException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, baseline, 44100));
        Assert.Equal(100, NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, Advance(baseline), 44100, baseline));
    }

    [Fact]
    public void StableHistoricalFaultAndReconnectCountDoNotPermanentlyBlockAudibleMusic()
    {
        var baseline = Playing() with { Diagnostics = Playing().Diagnostics! with { ReconnectCount = 2,
            LastFault = new("feedback_failed", Mini.Address, "feedback", "Earlier HTTP 503", true) } };
        Assert.Equal(100, NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, Advance(baseline), 44100, baseline));
    }

    [Theory]
    [InlineData("new-feedback-error")]
    [InlineData("new-sync-error")]
    [InlineData("reset-feedback")]
    [InlineData("reset-packets")]
    [InlineData("unknown-counter")]
    [InlineData("negative-counter")]
    [InlineData("new-reconnect")]
    [InlineData("new-fault")]
    [InlineData("no-pcm-progress")]
    [InlineData("no-packet-progress")]
    [InlineData("different-applied")]
    [InlineData("short-window")]
    [InlineData("feedback-delayed")]
    public void UnstableOrUnknownSubsequentWindowStillCannotSaveAReference(string changed)
    {
        var baseline = Playing() with { Diagnostics = new(Transport: Transport(feedbackErrors: 1), Latency: Latency()) };
        var current = Advance(baseline); var diagnostics = current.Diagnostics!; var transport = diagnostics.Transport!; var member = transport.Members[0];
        current = changed switch {
            "new-feedback-error" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { FeedbackFailures = 2 }] } } },
            "new-sync-error" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { SyncErrors = 1 }] } } },
            "reset-feedback" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { FeedbackFailures = 0 }] } } },
            "reset-packets" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { PacketsSent = 1 }] } } },
            "unknown-counter" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { RetransmitQueueDrops = null }] } } },
            "negative-counter" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { SendErrors = -1 }] } } },
            "new-reconnect" => current with { Diagnostics = diagnostics with { ReconnectCount = 1 } },
            "new-fault" => current with { Diagnostics = diagnostics with { LastFault = new("new_error", Mini.Address, "media", "New failure", true) } },
            "no-pcm-progress" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { NonzeroPcmPacketsSent = 10 }] } } },
            "no-packet-progress" => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { PacketsSent = 100 }] } } },
            "different-applied" => current with { Diagnostics = diagnostics with { Latency = Latency(200) } },
            "short-window" => current with { Diagnostics = diagnostics with { Transport = transport with { SessionUptimeMs = 10999 } } },
            _ => current with { Diagnostics = diagnostics with { Transport = transport with { Members = [member with { FeedbackDelayed = true }] } } }
        };
        Assert.Throws<NativeSetupPendingException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, current, 44100, baseline));
        Assert.Null(Binding.VerifiedBufferMs);
    }

    [Fact]
    public void MissingInitialMetricsCanOnlyUseTheLaterStrictZeroHistorySuccess()
    {
        var baseline = Playing() with { Diagnostics = Playing().Diagnostics! with { Transport = null } };
        Assert.Equal(100, NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, Playing(), 44100, baseline));
        var historical = Playing() with { Diagnostics = new(Transport: Transport(feedbackErrors: 1), Latency: Latency()) };
        Assert.Throws<NativeSetupPendingException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, historical, 44100, baseline));
    }

    [Fact]
    public void BaselineForAnotherReceiverCannotBeUsedToConfirmTheCurrentConnection()
    {
        var baseline = Playing() with { Receiver = Mini with { Id = "001122334455" } };
        Assert.Throws<InvalidOperationException>(() => NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, Playing(), 44100, baseline));
    }

    [Fact]
    public async Task CancellingTheFiveSecondConfirmationWindowStopsPreviewWithoutSavingOrBecomingReady()
    {
        var platform = new WindowPlatform();
        await using var flow = new NativeSetupFlow(platform);
        await flow.RefreshAsync(default); await flow.ConnectAsync(Mini, default);
        using var cancellation = new CancellationTokenSource();
        var completing = flow.ConfirmMusicAsync(cancellation.Token);
        await platform.Entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing);
        Assert.False(platform.Saved); Assert.Equal(1, platform.Stops); Assert.NotEqual(NativeSetupStep.Ready, flow.Step);
    }

    private static SessionSnapshot Advance(SessionSnapshot baseline) => baseline with {
        Diagnostics = baseline.Diagnostics! with { Transport = baseline.Diagnostics!.Transport! with {
            SessionUptimeMs = 11000, Members = [baseline.Diagnostics.Transport!.Members[0] with {
                PacketsSent = 400, NonzeroPcmPacketsSent = 80, FeedbackDelayed = false }] } }
    };

    private sealed class WindowPlatform : INativeSetupPlatform
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Saved;
        public int Stops;
        public Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation)
            => Task.FromResult(new NativeSetupInspection(true, true, Binding.Endpoint, Binding, false));
        public Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<Receiver>>([Mini]);
        public Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation) => Task.CompletedTask;
        public async Task CompleteAsync(CancellationToken cancellation)
        {
            var baseline = Playing() with { Diagnostics = new(Transport: Transport(feedbackErrors: 1), Latency: Latency()) };
            Entered.SetResult(); await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            _ = NativeMusicConfirmation.ConfirmableAppliedMs(Binding, Mini, Advance(baseline), 44100, baseline);
            Saved = true;
        }
        public Task StopPreviewAsync(CancellationToken cancellation) { Stops++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
