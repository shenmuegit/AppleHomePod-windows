using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LocalMuteGuardTests
{
    [Fact]
    public void StalledAfterMuteRestoresOnlyOnce()
    {
        var guard = new LocalMuteGuard();
        Assert.Equal(LocalMuteAction.None, guard.Observe(0));
        Assert.Equal(LocalMuteAction.Mute, guard.Observe(10));
        Assert.Equal(LocalMuteAction.None, guard.Observe(10));
        Assert.Equal(LocalMuteAction.RestoreAndWarn, guard.Observe(10));
        Assert.Equal(LocalMuteAction.None, guard.Observe(11));
    }
    [Fact]
    public void AdvancingSamplesResetTheStallCount()
    {
        var guard = new LocalMuteGuard();
        Assert.Equal(LocalMuteAction.Mute, guard.Observe(10));
        Assert.Equal(LocalMuteAction.None, guard.Observe(10));
        Assert.Equal(LocalMuteAction.None, guard.Observe(11));
        Assert.Equal(LocalMuteAction.None, guard.Observe(11));
        Assert.Equal(LocalMuteAction.None, guard.Observe(12));
    }
    [Fact]
    public void MissingMetricsNeverMute()
    {
        var guard = new LocalMuteGuard();
        Assert.Equal(LocalMuteAction.None, guard.Observe(0));
        Assert.Equal(LocalMuteAction.None, guard.Observe(-1));
    }
}
