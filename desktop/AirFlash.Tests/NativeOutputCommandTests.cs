using AirFlash.App;
using AirFlash.App.Services;
using AirFlash.App.Verification;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeOutputCommandTests
{
    [Theory]
    [InlineData("--native-check", true)]
    [InlineData("--native-preflight-check", true)]
    [InlineData("--native-monitor-check", true)]
    [InlineData("--native-enable", false)]
    [InlineData("--native-disable", false)]
    [InlineData("--native-bind", false)]
    [InlineData("--native-unbind", false)]
    [InlineData("--native-calibrate", false)]
    public void PureNativeChecksOnlyDispatchInspectionCommands(string command, bool inspection)
    {
        Assert.Equal(inspection, NativeOutputCheck.IsCommand([command]));
        Assert.Equal(!inspection, NativeOutputMaintenance.IsCommand([command]));
        Assert.True(Program.IsDiagnostic([command]));
        NativeOutputMaintenance.ValidateCommand([command]);
    }

    [Fact]
    public void MixedOrDuplicateCommandsAreRejectedBeforeMaintenance()
    {
        string[][] invalid = [[], ["--native-bind", "--native-check"], ["--native-calibrate", "--native-monitor-check"],
            ["--native-enable", "--native-disable"], ["--native-check", "--native-preflight-check"], ["--native-bind", "--native-bind"]];
        Assert.All(invalid, args => Assert.Throws<ArgumentException>(() => NativeOutputMaintenance.ValidateCommand(args)));
    }
}
