using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class MuteRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "applehome-mute-tests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "local-mute.json");
    [Fact]
    public void OriginalStateSurvivesRestartAndIsNeverOverwritten()
    {
        var journal = new MuteRecoveryJournal(JournalPath);
        journal.Remember("speaker", false); journal.Remember("speaker", true);
        var restarted = new MuteRecoveryJournal(JournalPath);
        Assert.False(restarted.Entries["speaker"]);
        Assert.Equal(false, restarted.RestoreValue("speaker", true));
        Assert.Null(restarted.RestoreValue("speaker", false));
        restarted.Forget("speaker");
        Assert.Empty(new MuteRecoveryJournal(JournalPath).Entries);
    }
    [Theory]
    [InlineData("broken")]
    [InlineData("{\"schema_version\":2,\"entries\":{}}")]
    [InlineData("{\"schema_version\":1,\"entries\":null}")]
    public void InvalidJournalIsPreservedAndRejected(string contents)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(JournalPath, contents);
        Assert.Throws<InvalidDataException>(() => new MuteRecoveryJournal(JournalPath));
        Assert.Equal(contents, File.ReadAllText(JournalPath));
    }
    [Fact]
    public void OversizeJournalIsRejectedBeforeParsing()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(JournalPath, new string(' ', 65537));
        Assert.Throws<InvalidDataException>(() => new MuteRecoveryJournal(JournalPath));
    }
    [Fact]
    public void FailedWriteDoesNotClaimMuteOwnership()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(Path.Combine(_directory, "blocker"), "file");
        var journal = new MuteRecoveryJournal(Path.Combine(_directory, "blocker", "local-mute.json"));
        Assert.ThrowsAny<IOException>(() => journal.Remember("speaker", false));
        Assert.Empty(journal.Entries);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
