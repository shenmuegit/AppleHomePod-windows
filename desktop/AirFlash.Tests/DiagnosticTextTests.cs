using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void ExportRemovesDeviceIdentityWithoutCorruptingMetrics()
    {
        var json = JsonSerializer.Serialize(new { host = "192.0.2.42", device_id = "AABBCCDDEEFF", name = "Private room", pin = 123456,
            private_key = "synthetic-key", packets_sent = 123456789012L, message = "Connection to 192.0.2.42 failed" });
        using var document = JsonDocument.Parse(DiagnosticText.ForExport(json));
        foreach (var label in new[] { "host", "device_id", "name" }) Assert.Equal("[device]", document.RootElement.GetProperty(label).GetString());
        Assert.Equal(123456789012L, document.RootElement.GetProperty("packets_sent").GetInt64());
        Assert.DoesNotContain("192.0.2.42", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("[redacted]", document.RootElement.GetProperty("private_key").GetString());
    }

    [Fact]
    public void ProtectsJsonSecretsAndEscapedPathsWithoutCorruptingJson()
    {
        var json = JsonSerializer.Serialize(new { pin = 123456, private_key = "secret-material", password = "p\"ass", path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), clean = "startup failed" });
        using var value = JsonDocument.Parse(DiagnosticText.Protect(json));
        foreach (var label in new[] { "pin", "private_key", "password" }) Assert.Equal("[redacted]", value.RootElement.GetProperty(label).GetString());
        Assert.Equal("[user-path]", value.RootElement.GetProperty("path").GetString());
        Assert.Equal("startup failed", value.RootElement.GetProperty("clean").GetString());
    }

    [Fact]
    public void ProtectsLabelledStderr()
    {
        var safe = DiagnosticText.Protect("startup error pin=123456 public_key=abc password=\"secret value\"");
        Assert.DoesNotContain("123456", safe); Assert.DoesNotContain("abc", safe); Assert.DoesNotContain("secret value", safe);
        Assert.Contains("startup error", safe);
    }
}
