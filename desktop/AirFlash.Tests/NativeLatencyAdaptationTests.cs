using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeLatencyAdaptationTests
{
    private static Receiver Mini => new("aabbccddeeff", "da", "192.0.2.1")
        { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
    private static NativeBinding Binding => NativeEngineHostTests.Binding with { ReceiverId = Mini.Id };

    [Fact]
    public void UnreportedQualifiedMiniStartsFromItsModelHintWithoutInventingAReference()
    {
        var choice = NativeLatencyAdaptation.SelectUnreported(Mini, 44100);
        Assert.Equal(100, choice.Milliseconds);
        Assert.Equal("model_profile", choice.Source);
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(Binding, Mini, 44100));
        Assert.Null(Binding.VerifiedBufferMs);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("firmware")]
    [InlineData("group")]
    [InlineData("stereo")]
    [InlineData("leader")]
    [InlineData("codec")]
    [InlineData("rate")]
    public void QualifiedModelHintCannotBecomeAGlobalBufferDefault(string changed)
    {
        var receiver = Changed(changed);
        var choice = NativeLatencyAdaptation.SelectUnreported(receiver, changed == "rate" ? 48000 : 44100);
        Assert.Equal(250, choice.Milliseconds);
        Assert.Equal("compatibility_fallback", choice.Source);
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(Binding, receiver, 44100));
    }

    [Theory]
    [InlineData(100, 200)]
    [InlineData(250, 500)]
    [InlineData(1000, 2000)]
    [InlineData(1500, 2000)]
    [InlineData(2000, null)]
    public void RecoveryRaisesOnlyTheCurrentCandidateAndStopsAtTheProductBound(int current, int? next)
        => Assert.Equal(next, NativeLatencyAdaptation.NextCandidate(current));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2001)]
    public void RecoveryRejectsInvalidCurrentValues(int current)
        => Assert.Throws<ArgumentOutOfRangeException>(() => NativeLatencyAdaptation.NextCandidate(current));

    [Fact]
    public void ExplicitMusicRecoveryCannotSilentlyDownshiftTheBaseChoice()
    {
        var choice = NativeLatencyAdaptation.SelectUnreported(Mini, 44100, 200);
        Assert.Equal(200, choice.Milliseconds);
        Assert.Equal("music_recovery", choice.Source);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLatencyAdaptation.SelectUnreported(Mini, 44100, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLatencyAdaptation.SelectUnreported(Mini with { Model = "unknown" }, 44100, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLatencyAdaptation.SelectUnreported(Mini, 44100, 2001));
    }

    [Fact]
    public void FingerprintUsesNormalizedIdentityAndIgnoresAddressAndDisplayName()
    {
        var moved = Mini with { Id = "AA:BB:CC:DD:EE:FF", Name = "user name", Address = "192.0.2.99" };
        Assert.Equal(NativeLatencyAdaptation.ProfileKey(Mini, 44100), NativeLatencyAdaptation.ProfileKey(moved, 44100));
        Assert.Matches("^[0-9a-f]{64}$", NativeLatencyAdaptation.ProfileKey(Mini, 44100));
        Assert.NotEqual(NativeLatencyAdaptation.ProfileKey(Mini with { Model = "x|y", SoftwareVersion = "z" }, 44100),
            NativeLatencyAdaptation.ProfileKey(Mini with { Model = "x", SoftwareVersion = "y|z" }, 44100));
    }

    [Fact]
    public void FingerprintRejectsUnboundedOrMalformedDiscoveryContext()
    {
        Assert.Throws<ArgumentException>(() => NativeLatencyAdaptation.ProfileKey(Mini with { Model = new string('x', 257) }, 44100));
        Assert.Throws<ArgumentException>(() => NativeLatencyAdaptation.ProfileKey(Mini with { Id = "receiver\nother" }, 44100));
        Assert.Throws<ArgumentException>(() => NativeLatencyAdaptation.ProfileKey(Mini with { Members = [Mini, Mini, Mini] }, 44100));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("model")]
    [InlineData("firmware")]
    [InlineData("group")]
    [InlineData("stereo")]
    [InlineData("leader")]
    [InlineData("codec")]
    [InlineData("rate")]
    public void ScopedReferenceCannotFallBackToOldValueAfterItsContextChanges(string changed)
    {
        var binding = Binding with { VerifiedBufferMs = 200, VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Mini, 44100) };
        Assert.Equal(200, NativeLatencyAdaptation.VerifiedPreference(binding, Mini, 44100));
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Changed(changed), changed == "rate" ? 48000 : 44100));
    }

    [Fact]
    public void LegacyReferenceIsRetainedOnlyForItsBoundQualifiedMiniAtTheQualifiedRate()
    {
        var binding = Binding with { VerifiedBufferMs = 100 };
        Assert.Equal(100, NativeLatencyAdaptation.VerifiedPreference(binding, Mini, 44100));
        Assert.Equal(0, NativeLatencyAdaptation.VerifiedPreference(binding with { VerifiedBufferMs = 0 }, Mini, 44100));
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Mini, 48000));
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Changed("identity"), 44100));
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Changed("firmware"), 44100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8000)]
    [InlineData(96000)]
    public void AScopedReferenceDoesNotSurviveAnUnsupportedSampleRate(int rate)
    {
        var binding = Binding with { VerifiedBufferMs = 100, VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Mini, 44100) };
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Mini, rate));
    }

    [Fact]
    public void NormalMusicConfirmationSavesActualAppliedValueWithItsCurrentProfile()
    {
        var saved = NativeLatencyAdaptation.ConfirmReference(Binding, Mini, 44100, 175);
        Assert.Equal(175, saved.VerifiedBufferMs);
        Assert.Equal(NativeLatencyAdaptation.ProfileKey(Mini, 44100), saved.VerifiedBufferProfile);
        Assert.Equal(175, NativeLatencyAdaptation.VerifiedPreference(saved, Mini, 44100));
        Assert.Null(Binding.VerifiedBufferMs);
        Assert.Equal(Binding.Calibration, saved.Calibration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmationDoesNotReplaceAnExistingMatchingKnownReference(bool scoped)
    {
        var binding = Binding with { VerifiedBufferMs = 100, VerifiedBufferProfile = scoped ? NativeLatencyAdaptation.ProfileKey(Mini, 44100) : null };
        Assert.Same(binding, NativeLatencyAdaptation.ConfirmReference(binding, Mini, 44100, 250));
    }

    [Fact]
    public void ConfirmationCanReplaceAReferenceForAnObsoleteProfileWithoutUsingItsValue()
    {
        var binding = Binding with { VerifiedBufferMs = 150, VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Changed("firmware"), 44100) };
        Assert.Null(NativeLatencyAdaptation.VerifiedPreference(binding, Mini, 44100));
        var saved = NativeLatencyAdaptation.ConfirmReference(binding, Mini, 44100, 200);
        Assert.Equal(200, saved.VerifiedBufferMs);
        Assert.Equal(NativeLatencyAdaptation.ProfileKey(Mini, 44100), saved.VerifiedBufferProfile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void ConfirmationRequiresAnAppliedPositiveValue(int applied)
        => Assert.Throws<ArgumentOutOfRangeException>(() => NativeLatencyAdaptation.ConfirmReference(Binding, Mini, 44100, applied));

    [Fact]
    public void ConfirmationCannotAttachAReferenceToAnotherOrUnqualifiedReceiver()
    {
        Assert.Throws<InvalidOperationException>(() => NativeLatencyAdaptation.ConfirmReference(Binding, Changed("identity"), 44100, 100));
        Assert.Throws<InvalidOperationException>(() => NativeLatencyAdaptation.ConfirmReference(Binding, Changed("firmware"), 44100, 100));
        Assert.Throws<InvalidOperationException>(() => NativeLatencyAdaptation.ConfirmReference(Binding, Mini with { Online = false }, 44100, 100));
        Assert.Throws<InvalidOperationException>(() => NativeLatencyAdaptation.ConfirmReference(Binding, Mini, 48000, 100));
    }

    private static Receiver Changed(string field) => field switch {
        "identity" => Mini with { Id = "112233445566" },
        "model" => Mini with { Model = "AudioAccessory6,1" },
        "firmware" => Mini with { SoftwareVersion = "27.1" },
        "group" => Mini with { GroupId = "group-1" },
        "stereo" => Mini with { StereoId = "stereo-1" },
        "leader" => Mini with { IsLeader = false },
        "codec" => Mini with { Codecs = [2] },
        _ => Mini
    };
}
