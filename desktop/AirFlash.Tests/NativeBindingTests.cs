using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeBindingTests
{
    [Fact]
    public void MixFormatIncludesAllBytesUsedByTheAudioEngine()
    {
        byte[] pcm = [3,0,2,0,68,172,0,0,32,98,5,0,8,0,32,0,0,0];
        var first = NativeBindingPolicy.ReadMixFormat(pcm);
        Assert.Equal(2, first.Channels);
        Assert.StartsWith("44100/2/32/3/", first.MixFormat);
        byte[] extended = new byte[40];
        pcm.CopyTo(extended, 0); extended[0] = 254; extended[1] = 255; extended[16] = 22; extended[24] = 3;
        var stereo = NativeBindingPolicy.ReadMixFormat(extended);
        extended[20] = 3;
        Assert.NotEqual(stereo.MixFormat, NativeBindingPolicy.ReadMixFormat(extended).MixFormat);
        Assert.Throws<InvalidDataException>(() => NativeBindingPolicy.ReadMixFormat([3,0]));
    }

    private static EndpointIdentity Candidate => new("endpoint-1", @"ROOT\MEDIA\0009", "VBAudioVACWDM",
        "VB-Audio Software", "3.3.1.7", "f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5",
        "CABLE Input", "CABLE Input (VB-Audio Virtual Cable)", "44100/2/32/3", 2, AudioEndpointKind.Playback, true);
    private static NativeBinding Binding => new(1, "receiver-1", Candidate, Candidate.FriendlyName, "HomePod – da",
        new(false, Candidate.MixFormat, Candidate.DriverSha256, 0, 38));

    [Fact]
    public void LegacyBindingDoesNotInventAVerifiedBuffer()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(Binding, AppSettings.JsonOptions)!.AsObject();
        json.Remove("verified_buffer_ms");
        var loaded = System.Text.Json.JsonSerializer.Deserialize<NativeBinding>(json, AppSettings.JsonOptions)!;
        Assert.Null(loaded.VerifiedBufferMs); Assert.Null(NativeBindingPolicy.ValidateStored(loaded));
        Assert.Null(NativeBindingPolicy.PlaybackQualification(loaded));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2001)]
    public void InvalidDeviceBufferCannotQualify(int bufferMs)
    {
        var binding = Binding with { VerifiedBufferMs = bufferMs };
        Assert.NotNull(NativeBindingPolicy.ValidateStored(binding));
        Assert.NotNull(NativeBindingPolicy.PlaybackQualification(binding));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(75)]
    [InlineData(500)]
    [InlineData(2000)]
    public void VerifiedBufferRoundTripsWithItsBinding(int bufferMs)
    {
        var binding = Binding with { VerifiedBufferMs = bufferMs };
        var loaded = System.Text.Json.JsonSerializer.Deserialize<NativeBinding>(System.Text.Json.JsonSerializer.Serialize(binding, AppSettings.JsonOptions), AppSettings.JsonOptions)!;
        Assert.Equal(bufferMs, loaded.VerifiedBufferMs); Assert.Null(NativeBindingPolicy.PlaybackQualification(loaded));
        Assert.NotNull(NativeBindingPolicy.PlaybackQualification(loaded with { Calibration = null }));
    }

    [Fact]
    public void RebindingPreservesTimingOnlyWhileVolumeCalibrationStillMatches()
    {
        var previous = Binding with { VerifiedBufferMs = 125 };
        var same = NativeBindingPolicy.PrepareBinding(Candidate, previous.ReceiverId, previous.AssignedName, previous, new HashSet<string>());
        Assert.Equal(125, same.VerifiedBufferMs);
        var changed = NativeBindingPolicy.PrepareBinding(Candidate with { MixFormat = "48000/2/32/3" }, previous.ReceiverId, previous.AssignedName, previous, new HashSet<string>());
        Assert.Null(changed.Calibration); Assert.Null(changed.VerifiedBufferMs);
    }

    [Theory]
    [InlineData("192.0.2.1:7000")]
    [InlineData("auto-endpoint:192.0.2.1:7000")]
    [InlineData("stereo:group-id")]
    public void EndpointBasedReceiverIdsCannotBeBound(string receiverId)
    {
        Assert.NotNull(NativeBindingPolicy.ValidateStored(Binding with { ReceiverId = receiverId }));
    }

    [Fact]
    public void NewVerifiedPlaybackEndpointCanBeClaimed()
    {
        Assert.True(NativeBindingPolicy.CanClaim(Candidate, new HashSet<string>()));
        Assert.True(NativeBindingPolicy.CanClaim(Candidate with { FriendlyName = "用户另取的名称" }, new HashSet<string>()));
    }

    [Fact]
    public void PreexistingAndSixteenChannelEndpointsAreRejected()
    {
        Assert.False(NativeBindingPolicy.CanClaim(Candidate, new HashSet<string> { @"root\media\0009" }));
        Assert.False(NativeBindingPolicy.CanClaim(Candidate with { Channels = 16 }, new HashSet<string>()));
        Assert.False(NativeBindingPolicy.CanClaim(Candidate with { PinName = "CABLE In16Ch" }, new HashSet<string>()));
        Assert.False(NativeBindingPolicy.CanClaim(Candidate with { Kind = AudioEndpointKind.Recording }, new HashSet<string>()));
        Assert.False(NativeBindingPolicy.CanClaim(Candidate with { Active = false }, new HashSet<string>()));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("hardware")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("adapter")]
    public void SimilarNameCannotSubstituteForVerifiedDriverFacts(string changed)
    {
        var candidate = Candidate with { FriendlyName = "HomePod – da" };
        candidate = changed switch {
            "provider" => candidate with { Provider = "Another vendor" },
            "hardware" => candidate with { HardwareId = "Another driver" },
            "version" => candidate with { DriverVersion = "1.0" },
            "hash" => candidate with { DriverSha256 = new string('0', 64) },
            _ => candidate with { AdapterInstanceId = "" }
        };
        Assert.False(NativeBindingPolicy.CanClaim(candidate, new HashSet<string>()));
    }

    [Fact]
    public void UserRenameIsNotUndone()
    {
        var renamed = Candidate with { FriendlyName = "我的输出 (VB-Audio Virtual Cable)", EndpointLabel = "我的输出" };
        var adopted = NativeBindingPolicy.PrepareBinding(renamed,"receiver-1","HomePod – da",Binding,new HashSet<string>());
        Assert.Equal("我的输出",adopted.AssignedName);
        Assert.Equal(renamed.FriendlyName,adopted.Endpoint.FriendlyName);
        Assert.Equal(Binding.OriginalName,adopted.OriginalName);
        Assert.Equal(Binding.Calibration,adopted.Calibration);
    }

    [Fact]
    public void InitialSystemNameMustBeChosenBeforeTheBindingIsActivated()
    {
        Assert.Throws<InvalidOperationException>(() => NativeBindingPolicy.PrepareBinding(
            Candidate with { EndpointLabel="CABLE Input" },"receiver-1","HomePod – da",null,new HashSet<string>()));
        var renamed=Candidate with { EndpointLabel="HomePod – da",FriendlyName="HomePod – da (VB-Audio Virtual Cable)" };
        var binding=NativeBindingPolicy.PrepareBinding(renamed,"receiver-1","HomePod – da",null,new HashSet<string>());
        Assert.Equal("HomePod – da",binding.AssignedName);
        Assert.Null(binding.Calibration);
    }

    [Fact]
    public void AUserLabelCannotDisguiseTheSixteenChannelDriverPin()
    {
        var renamed=Candidate with { EndpointLabel="HomePod – da",FriendlyName="HomePod – da",PinName="CABLE In 16 Ch",Channels=2 };
        Assert.Throws<InvalidOperationException>(() => NativeBindingPolicy.PrepareBinding(
            renamed,"receiver-1","HomePod – da",null,new HashSet<string>()));
    }

    [Fact]
    public void RebindingCannotReplaceAnotherReceiverOrAdapter()
    {
        var renamed=Candidate with { EndpointLabel="HomePod – da" };
        Assert.Throws<InvalidOperationException>(()=>NativeBindingPolicy.PrepareBinding(renamed,"other-receiver","HomePod – da",Binding,new HashSet<string>()));
        Assert.Throws<InvalidOperationException>(()=>NativeBindingPolicy.PrepareBinding(renamed with { AdapterInstanceId="other-adapter" },"receiver-1","HomePod – da",Binding,new HashSet<string>()));
    }

    [Fact]
    public void DriverOrMixChangeInvalidatesCalibration()
    {
        Assert.Null(NativeBindingPolicy.ValidateLive(Binding, Candidate with { FriendlyName = "My name" }));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding, Candidate with { DriverSha256 = new string('0', 64) }));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding, Candidate with { MixFormat = "48000/2/32/3" }));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding, Candidate with { EndpointId = "endpoint-2" }));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding with { Calibration = null }, Candidate));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding with { ReceiverId = "" }, Candidate));
        Assert.NotNull(NativeBindingPolicy.ValidateLive(Binding with { Calibration = Binding.Calibration! with { ResumeDeviceVolume = 0 } }, Candidate));
    }

    [Fact]
    public async Task BindingRoundtripKeepsUncalibratedStateUnusable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "applehome-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var store = new NativeBindingStore(Path.Combine(directory, "binding.json"));
            Assert.Null(store.Load());
            await store.SaveAsync(Binding with { Calibration = null }, CancellationToken.None);
            Assert.NotNull(NativeBindingPolicy.ValidateLive(store.Load()!, Candidate));
            await store.SaveAsync(Binding, CancellationToken.None);
            Assert.Equal(Binding, store.Load());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CanceledSavePreservesPreviousBinding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "applehome-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var store = new NativeBindingStore(Path.Combine(directory, "binding.json"));
            await store.SaveAsync(Binding, CancellationToken.None);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Binding with { ReceiverId = "new" }, canceled.Token));
            Assert.Equal(Binding, store.Load());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":1,\"receiver_id\":\"receiver-1\"}")]
    [InlineData("null")]
    public void BrokenBindingNeverBecomesAnEmptyUsableConfiguration(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "applehome-binding-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            File.WriteAllText(path, json);
            Assert.ThrowsAny<Exception>(() => new NativeBindingStore(path).Load());
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingCalibrationBooleanCannotSilentlyChooseAGainModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "applehome-binding-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Binding, AppSettings.JsonOptions))!;
            json["calibration"]!.AsObject().Remove("master_in_pcm");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<JsonException>(() => new NativeBindingStore(path).Load());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OversizeBindingIsRejectedBeforeJsonParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), "applehome-binding-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            File.WriteAllText(path, new string(' ', 65537));
            Assert.Throws<InvalidDataException>(() => new NativeBindingStore(path).Load());
        }
        finally { File.Delete(path); }
    }
}
