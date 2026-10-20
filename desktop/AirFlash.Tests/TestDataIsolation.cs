using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using AirFlash.App.Services;
namespace AirFlash.Tests;

internal static class TestDataIsolation
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "The test assembly isolates production diagnostics before any test runs.")]
    internal static void Initialize() => AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), "applehome-tests", Guid.NewGuid().ToString("N"));
}
