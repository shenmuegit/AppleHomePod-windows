using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.Services.Setup;
using Xunit;
namespace AirFlash.Tests;

public sealed class AppAssemblyBoundaryTests
{
    [Fact]
    public void ProductionAssemblyTestsKeepDiagnosticsOutOfTheUserInstallation()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsAndSetupTestsExerciseProductionTypes()
    {
        Type[] productionTypes = [typeof(VolumeSliderMath), typeof(TrayRegistration), typeof(WindowsDiscovery),
            typeof(WindowsDnsSdApi), typeof(ManagedDnsSdApi), typeof(MdnsPacket), typeof(WindowsCapabilities),
            typeof(AppPaths), typeof(SingleInstance), typeof(NetworkAdapterCatalog), typeof(NativeSetupAudioLifetime)];
        Assert.All(productionTypes, type => Assert.Equal("AirFlash", type.Assembly.GetName().Name));
    }

    [Fact]
    public void WindowsServicesResolveTheProductionNoticesAndIconResources()
    {
        var assembly = typeof(AppPaths).Assembly;
        using var notices = assembly.GetManifestResourceStream("AirFlash.ThirdPartyNotices.txt");
        Assert.NotNull(notices);
        using var reader = new StreamReader(notices);
        Assert.Contains("AppleHome modifications are licensed under GNU GPL version 3 or later.", reader.ReadToEnd());
        using var icon = assembly.GetManifestResourceStream("AirFlash.Icon.ico");
        Assert.NotNull(icon);
        var header = new byte[4];
        icon.ReadExactly(header);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, header);
    }
}
