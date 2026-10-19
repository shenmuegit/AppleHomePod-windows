using AirFlash.Core;
using System.Text.Json;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeSetupOutputTests
{
    [Fact]
    public void WindowsFloatReadbackRoundingDoesNotBreakConfirmationOrRollback()
    {
        var output = new Fake { Volume = (.08f, true) };
        output.AfterVolumeScalar = () => output.Volume = (output.Volume.Scalar + .0000001f, output.Volume.Muted);
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input")) setup.SelectForConfirmation();
        Assert.InRange(output.Volume.Scalar, .079999f, .080001f); Assert.True(output.Volume.Muted);
    }
    [Fact]
    public void PreparesNameAndRoutesOnlyRenderRolesBeforeConfirmation()
    {
        var output = new Fake(); using var setup = new NativeSetupOutput(output, "bound", "CABLE Input");
        setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
        Assert.Equal("HomePod – da", output.Name);
        Assert.Equal("bound", output.Defaults[0]); Assert.Equal("bound", output.Defaults[1]);
        Assert.Equal("headset", output.Defaults[2]);
        Assert.Equal(.54f, output.Volume.Scalar); Assert.False(output.Volume.Muted);
    }

    [Fact]
    public void CancelRestoresOnlyChangesStillOwnedBySetup()
    {
        var output = new Fake { Volume = (0, true) };
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input"))
        {
            setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
            Assert.InRange(output.Volume.Scalar, .3f, .6f); Assert.False(output.Volume.Muted);
        }
        Assert.Equal("CABLE Input", output.Name); Assert.Equal("speakers", output.Defaults[0]);
        Assert.Equal("speakers", output.Defaults[1]); Assert.Equal((0f, true), output.Volume);
    }

    [Fact]
    public void ExternalChangesArePreservedDuringRollback()
    {
        var output = new Fake();
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input"))
        {
            setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
            output.Name = "My speaker"; output.Defaults[0] = "headphones"; output.Volume = (.42f, true);
        }
        Assert.Equal("My speaker", output.Name); Assert.Equal("headphones", output.Defaults[0]);
        Assert.Equal("speakers", output.Defaults[1]); Assert.Equal((.42f, true), output.Volume);
    }

    [Fact]
    public void SuccessfulConfirmationKeepsConfiguredOutput()
    {
        var output = new Fake();
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input"))
        { setup.PrepareName("HomePod – da"); setup.SelectForConfirmation(); setup.Commit(); }
        Assert.Equal("HomePod – da", output.Name); Assert.Equal("bound", output.Defaults[0]);
    }

    [Fact]
    public void CustomNameIsPreservedAndRenameReadbackIsRequired()
    {
        var output = new Fake { Name = "客厅" };
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input")) setup.PrepareName("HomePod – da");
        Assert.Equal("客厅", output.Name);
        output.Name = "CABLE Input"; output.IgnoreNameWrite = true;
        using var failed = new NativeSetupOutput(output, "bound", "CABLE Input");
        Assert.Throws<IOException>(() => failed.PrepareName("HomePod – da"));
    }

    [Fact]
    public void PartialRoutingFailureRestoresEarlierSuccessfulMutation()
    {
        var output = new Fake { RejectMultimedia = true };
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input"))
        { setup.PrepareName("HomePod – da"); Assert.Throws<IOException>(setup.SelectForConfirmation); }
        Assert.Equal("speakers", output.Defaults[0]); Assert.Equal("CABLE Input", output.Name);
    }

    [Fact]
    public void CompleteSerializableIntentIsSavedBeforeEachWindowsWrite()
    {
        var output = new Fake { Volume = (.08f, true) };
        NativeSetupOutputState? journal = null;
        var snapshots = new List<NativeSetupOutputState>();
        var events = new List<string>();
        var json = new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true };
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => {
            Assert.NotNull(state);
            journal = JsonSerializer.Deserialize<NativeSetupOutputState>(JsonSerializer.Serialize(state, json), json)!;
            snapshots.Add(journal); events.Add("save");
        });
        output.BeforeWrite = change => {
            events.Add(change);
            Assert.NotNull(journal);
            Assert.Equal("bound", journal.EndpointId); Assert.Equal("CABLE Input", journal.PinName);
            Assert.Equal("CABLE Input", journal.OriginalName); Assert.Equal("HomePod – da", journal.AssignedName);
            if (change == "default:0") Assert.Equal("speakers", journal.OriginalDefaults[0]);
            if (change == "default:1") Assert.Equal("speakers", journal.OriginalDefaults[1]);
            if (change == "volume")
            {
                Assert.Equal(new NativeSetupOutputVolume(.08f, true), journal.OriginalVolume);
                Assert.Equal(new NativeSetupOutputVolume(.3f, false), journal.AssignedVolume);
            }
        };
        setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
        Assert.Equal(new[] { "save", "name", "save", "default:0", "save", "default:1", "save", "volume" }, events);
        Assert.Empty(snapshots[0].OriginalDefaults);
        Assert.Single(snapshots[1].OriginalDefaults);
        Assert.Null(snapshots[2].OriginalVolume);
    }

    [Fact]
    public void RecoveryAfterACrashRestoresOwnedValuesAndThenClearsTheJournal()
    {
        var output = new Fake { Volume = (.08f, true) };
        NativeSetupOutputState? journal = null;
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        crashed.PrepareName("HomePod – da"); crashed.SelectForConfirmation();
        var json = new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true };
        var persisted = JsonSerializer.Deserialize<NativeSetupOutputState>(JsonSerializer.Serialize(journal, json), json)!;
        NativeSetupOutput.Recover(output, persisted, state => journal = state);
        Assert.Equal("CABLE Input", output.Name);
        Assert.Equal("speakers", output.Defaults[0]); Assert.Equal("speakers", output.Defaults[1]);
        Assert.Equal("headset", output.Defaults[2]); Assert.Equal((.08f, true), output.Volume);
        Assert.Null(journal);
    }

    [Fact]
    public void RecoveryPreservesChangesMadeOutsideTheCrashedSetup()
    {
        var output = new Fake { Volume = (.08f, true) };
        NativeSetupOutputState? journal = null;
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        crashed.PrepareName("HomePod – da"); crashed.SelectForConfirmation();
        output.Name = "My speaker"; output.Defaults[0] = "headphones"; output.Volume = (.42f, true);
        NativeSetupOutput.Recover(output, journal!, state => journal = state);
        Assert.Equal("My speaker", output.Name); Assert.Equal("headphones", output.Defaults[0]);
        Assert.Equal("speakers", output.Defaults[1]); Assert.Equal((.42f, true), output.Volume);
        Assert.Null(journal);
    }

    [Fact]
    public void RecoveryRestoresAScalarWrittenBeforeTheMuteSetterCrashed()
    {
        var output = new Fake { Volume = (.08f, true) }; NativeSetupOutputState? journal = null;
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        output.AfterVolumeScalar = () => throw new IOException("crash between volume setters");
        Assert.Throws<IOException>(crashed.SelectForConfirmation);
        Assert.Equal((.3f, true), output.Volume); Assert.NotNull(journal);
        output.AfterVolumeScalar = null;
        NativeSetupOutput.Recover(output, journal, state => journal = state);
        Assert.Equal((.08f, true), output.Volume); Assert.Null(journal);
    }

    [Fact]
    public void ASecondRecoveryCompletesAMuteRestoreAfterTheScalarWasAlreadyRestored()
    {
        var output = new Fake { Volume = (.08f, true) }; NativeSetupOutputState? journal = null;
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        crashed.SelectForConfirmation();
        output.AfterVolumeScalar = () => throw new IOException("crash between restore setters");
        Assert.Throws<AggregateException>(() => NativeSetupOutput.Recover(output, journal!, state => journal = state));
        Assert.Equal((.08f, false), output.Volume); Assert.NotNull(journal);
        output.AfterVolumeScalar = null;
        NativeSetupOutput.Recover(output, journal, state => journal = state);
        Assert.Equal((.08f, true), output.Volume); Assert.Null(journal);
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("mute")]
    public void RecoveryPreservesEachExternallyChangedVolumePropertyIndependently(string changed)
    {
        var output = new Fake { Volume = changed == "scalar" ? (.42f, false) : (.3f, true) };
        var state = RecoveryState() with { OriginalVolume = new(.08f, changed == "scalar") };
        NativeSetupOutput.Recover(output, state, _ => { });
        Assert.Equal(changed == "scalar" ? (.42f, true) : (.08f, true), output.Volume);
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("mute")]
    public void RecoveryRecomputesEachVolumePropertyAfterPersistingIntent(string changed)
    {
        var output = new Fake { Volume = (.3f, false) };
        var state = RecoveryState() with { OriginalVolume = new(.08f, changed == "scalar") };
        NativeSetupOutput.Recover(output, state, saved => {
            if (saved is not null) output.Volume = changed == "scalar" ? (.42f, false) : (.3f, true);
        });
        Assert.Equal(changed == "scalar" ? (.42f, true) : (.08f, true), output.Volume);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("default:0")]
    [InlineData("default:1")]
    [InlineData("volume")]
    public void ACrashBetweenSavingIntentAndWritingWindowsIsRecoverable(string crashAt)
    {
        var output = new Fake { Volume = (.08f, true) };
        NativeSetupOutputState? journal = null;
        output.BeforeWrite = change => { if (change == crashAt) throw new IOException("simulated process interruption"); };
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        Assert.Throws<IOException>(() => { crashed.PrepareName("HomePod – da"); crashed.SelectForConfirmation(); });
        output.BeforeWrite = null;
        NativeSetupOutput.Recover(output, journal!, state => journal = state);
        Assert.Equal("CABLE Input", output.Name); Assert.Equal("speakers", output.Defaults[0]);
        Assert.Equal("speakers", output.Defaults[1]); Assert.Equal((.08f, true), output.Volume);
        Assert.Null(journal);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("default")]
    [InlineData("volume")]
    public void FailedIntentPersistenceMakesNoWindowsWrite(string change)
    {
        var output = new Fake { Volume = (.08f, true) };
        if (change == "volume") { output.Defaults[0] = "bound"; output.Defaults[1] = "bound"; }
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", _ => throw new IOException("disk unavailable"));
        Assert.Throws<IOException>(() => {
            if (change == "name") setup.PrepareName("HomePod – da");
            else setup.SelectForConfirmation();
        });
        Assert.Empty(output.Writes);
        Assert.Equal("CABLE Input", output.Name); Assert.Equal((.08f, true), output.Volume);
    }

    [Fact]
    public void FailureSavingTheNextIntentLeavesEarlierDurableIntentRecoverable()
    {
        var output = new Fake(); NativeSetupOutputState? journal = null; var saves = 0;
        var crashed = new NativeSetupOutput(output, "bound", "CABLE Input", state => {
            if (++saves == 2) throw new IOException("disk unavailable");
            journal = state;
        });
        crashed.PrepareName("HomePod – da");
        Assert.Throws<IOException>(crashed.SelectForConfirmation);
        Assert.Equal(new[] { "name" }, output.Writes);
        Assert.Empty(journal!.OriginalDefaults);
        NativeSetupOutput.Recover(output, journal, state => journal = state);
        Assert.Equal("CABLE Input", output.Name); Assert.Null(journal);
    }

    [Fact]
    public void FailedCommitClearDoesNotMarkTheChangesCommitted()
    {
        var output = new Fake(); NativeSetupOutputState? journal = null; var failClear = true;
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => {
            if (state is null && failClear) throw new IOException("journal clear failed");
            journal = state;
        });
        setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
        Assert.Throws<IOException>(setup.Commit);
        Assert.NotNull(journal);
        failClear = false; setup.Dispose();
        Assert.Equal("CABLE Input", output.Name); Assert.Equal("speakers", output.Defaults[0]);
        Assert.Null(journal);
    }

    [Fact]
    public void SuccessfulCommitClearsTheJournalBeforeKeepingTheConfiguredValues()
    {
        var output = new Fake(); NativeSetupOutputState? journal = null;
        using (var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state))
        {
            setup.PrepareName("HomePod – da"); setup.SelectForConfirmation(); setup.Commit();
            Assert.Null(journal);
        }
        Assert.Equal("HomePod – da", output.Name); Assert.Equal("bound", output.Defaults[0]);
    }

    [Fact]
    public void FailedRollbackKeepsTheJournalAndRecoveryCanRetryTheUnrestoredRole()
    {
        var output = new Fake { Volume = (.08f, true) }; NativeSetupOutputState? journal = null;
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        setup.PrepareName("HomePod – da"); setup.SelectForConfirmation();
        output.RejectMultimedia = true;
        Assert.Throws<AggregateException>(setup.Dispose);
        Assert.NotNull(journal);
        Assert.Equal("bound", output.Defaults[1]); Assert.Equal("speakers", output.Defaults[0]);
        Assert.Equal("CABLE Input", output.Name); Assert.Equal((.08f, true), output.Volume);
        output.RejectMultimedia = false;
        NativeSetupOutput.Recover(output, journal, state => journal = state);
        Assert.Equal("speakers", output.Defaults[1]); Assert.Null(journal);
    }

    [Fact]
    public void UnconfirmedRollbackWriteKeepsTheJournal()
    {
        var output = new Fake { Volume = (.08f, true) }; NativeSetupOutputState? journal = null;
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => journal = state);
        setup.SelectForConfirmation(); output.IgnoreVolumeWrite = true;
        Assert.Throws<AggregateException>(setup.Dispose);
        Assert.NotNull(journal); Assert.Equal((.3f, false), output.Volume);
    }

    [Fact]
    public void ARecoveryPersistenceFailureDoesNotMutateWindowsOrClearTheJournal()
    {
        var output = new Fake { Name = "HomePod – da", Volume = (.3f, false) };
        output.Defaults[0] = "bound"; output.Defaults[1] = "bound";
        var state = RecoveryState(); var clears = 0;
        Assert.Throws<AggregateException>(() => NativeSetupOutput.Recover(output, state, saved => {
            if (saved is null) clears++;
            throw new IOException("journal unavailable");
        }));
        Assert.Empty(output.Writes); Assert.Equal(0, clears);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("default:0")]
    [InlineData("default:1")]
    [InlineData("volume")]
    public void RecoveryRechecksOwnershipAfterTheJournalWrite(string change)
    {
        var output = new Fake { Name = "HomePod – da", Volume = (.3f, false) };
        output.Defaults[0] = "bound"; output.Defaults[1] = "bound";
        var state = RecoveryState() with {
            OriginalName = change == "name" ? "CABLE Input" : null,
            AssignedName = change == "name" ? "HomePod – da" : null,
            OriginalDefaults = change.StartsWith("default:", StringComparison.Ordinal)
                ? new Dictionary<int, string> { [change[^1] - '0'] = "speakers" } : new Dictionary<int, string>(),
            OriginalVolume = change == "volume" ? new(.08f, true) : null,
            AssignedVolume = change == "volume" ? new(.3f, false) : null
        };
        var cleared = false;
        NativeSetupOutput.Recover(output, state, saved => {
            if (saved is null) { cleared = true; return; }
            output.Name = "My speaker"; output.Defaults[0] = "headphones"; output.Defaults[1] = "headphones";
            output.Volume = (.42f, true);
        });
        Assert.Empty(output.Writes); Assert.True(cleared);
        Assert.Equal("My speaker", output.Name); Assert.Equal("headphones", output.Defaults[0]);
        Assert.Equal("headphones", output.Defaults[1]); Assert.Equal((.42f, true), output.Volume);
    }

    [Fact]
    public void FailedRollbackJournalClearRemainsRecoverableAfterWindowsWasRestored()
    {
        var output = new Fake(); NativeSetupOutputState? journal = null;
        var setup = new NativeSetupOutput(output, "bound", "CABLE Input", state => {
            if (state is null) throw new IOException("journal clear failed");
            journal = state;
        });
        setup.PrepareName("HomePod – da");
        Assert.Throws<AggregateException>(setup.Dispose);
        Assert.Equal("CABLE Input", output.Name); Assert.NotNull(journal);
        NativeSetupOutput.Recover(output, journal, state => journal = state);
        Assert.Null(journal);
    }

    [Theory]
    [InlineData("endpoint-empty")]
    [InlineData("endpoint-long")]
    [InlineData("endpoint-control")]
    [InlineData("pin-empty")]
    [InlineData("pin-long")]
    [InlineData("original-name-long")]
    [InlineData("assigned-name-control")]
    [InlineData("missing-original-name")]
    [InlineData("missing-assigned-name")]
    [InlineData("null-defaults")]
    [InlineData("communications-role")]
    [InlineData("negative-role")]
    [InlineData("missing-default-id")]
    [InlineData("long-default-id")]
    [InlineData("original-volume-nan")]
    [InlineData("assigned-volume-infinite")]
    [InlineData("original-volume-negative")]
    [InlineData("assigned-volume-above-one")]
    [InlineData("missing-original-volume")]
    [InlineData("missing-assigned-volume")]
    public void InvalidRecoveryStateIsRejectedBeforeReadingOrWritingWindows(string invalid)
    {
        var state = RecoveryState();
        state = invalid switch {
            "endpoint-empty" => state with { EndpointId = "" },
            "endpoint-long" => state with { EndpointId = new string('a', 2049) },
            "endpoint-control" => state with { EndpointId = "bound\n" },
            "pin-empty" => state with { PinName = " " },
            "pin-long" => state with { PinName = new string('a', 257) },
            "original-name-long" => state with { OriginalName = new string('a', 257) },
            "assigned-name-control" => state with { AssignedName = "name\n" },
            "missing-original-name" => state with { OriginalName = null },
            "missing-assigned-name" => state with { AssignedName = null },
            "null-defaults" => state with { OriginalDefaults = null! },
            "communications-role" => state with { OriginalDefaults = new Dictionary<int, string> { [2] = "speakers" } },
            "negative-role" => state with { OriginalDefaults = new Dictionary<int, string> { [-1] = "speakers" } },
            "missing-default-id" => state with { OriginalDefaults = new Dictionary<int, string> { [0] = null! } },
            "long-default-id" => state with { OriginalDefaults = new Dictionary<int, string> { [0] = new string('a', 2049) } },
            "original-volume-nan" => state with { OriginalVolume = new(float.NaN, true) },
            "assigned-volume-infinite" => state with { AssignedVolume = new(float.PositiveInfinity, false) },
            "original-volume-negative" => state with { OriginalVolume = new(-.1f, true) },
            "assigned-volume-above-one" => state with { AssignedVolume = new(1.1f, false) },
            "missing-original-volume" => state with { OriginalVolume = null },
            _ => state with { AssignedVolume = null }
        };
        var output = new Fake(); var saves = 0;
        Assert.Throws<InvalidDataException>(() => NativeSetupOutput.Recover(output, state, _ => saves++));
        Assert.Empty(output.Writes); Assert.Equal(0, output.Reads); Assert.Equal(0, saves);
    }

    private static NativeSetupOutputState RecoveryState() => new("bound", "CABLE Input", "CABLE Input", "HomePod – da",
        new Dictionary<int, string> { [0] = "speakers", [1] = "speakers" }, new(.08f, true), new(.3f, false));

    private sealed class Fake : INativeSetupOutput
    {
        public string Name = "CABLE Input";
        public readonly Dictionary<int, string?> Defaults = new() { [0] = "speakers", [1] = "speakers", [2] = "headset" };
        public (float Scalar, bool Muted) Volume = (.54f, false);
        public bool IgnoreNameWrite, IgnoreVolumeWrite, RejectMultimedia;
        public Action<string>? BeforeWrite;
        public Action? AfterVolumeScalar;
        public readonly List<string> Writes = [];
        public int Reads;
        public string ReadName() { Reads++; return Name; }
        public void WriteName(string name) { BeforeWrite?.Invoke("name"); Writes.Add("name"); if (!IgnoreNameWrite) Name = name; }
        public string? ReadDefault(int role) { Reads++; return Defaults[role]; }
        public void WriteDefault(int role, string id) { BeforeWrite?.Invoke($"default:{role}"); Writes.Add($"default:{role}"); if (role == 1 && RejectMultimedia) throw new IOException("route denied"); Defaults[role] = id; }
        public (float Scalar, bool Muted) ReadVolume() { Reads++; return Volume; }
        public void WriteVolume(float scalar, bool muted)
        {
            BeforeWrite?.Invoke("volume"); Writes.Add("volume");
            if (IgnoreVolumeWrite) return;
            Volume = (scalar, Volume.Muted); AfterVolumeScalar?.Invoke(); Volume = (Volume.Scalar, muted);
        }
    }
}
