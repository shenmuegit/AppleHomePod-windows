using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void NativeSnapshotExportHidesOpaqueSessionAndRenderRoleIds()
    {
        var text = "{\"capture_id\":\"private-capture\",\"instance_id\":\"private-session\",\"default_render_ids\":[null,\"private-render\",null],\"process_id\":42}";
        var safe = DiagnosticText.ForExport(text);
        Assert.DoesNotContain("private-", safe);
        using var value = JsonDocument.Parse(safe);
        Assert.Equal(42, value.RootElement.GetProperty("process_id").GetInt32());
        Assert.Equal(JsonValueKind.Null, value.RootElement.GetProperty("default_render_ids")[0].ValueKind);
    }
    [Fact]
    public void NativeEndpointIdentityIsRemovedFromExports()
    {
        var json = JsonSerializer.Serialize(new { endpoint_id = "private-endpoint", adapter_instance_id = "private-adapter",
            session_instance_id = "private-session", receiver_id = "private-receiver", friendly_name = "Private room",
            original_name = "Private original", assigned_name = "Private assigned", endpoint_label="Private endpoint label", driver_version = "3.3.1.7", channels = 2 });
        var safe = DiagnosticText.ForExport(json);
        Assert.DoesNotContain("private-", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private room", safe);
        Assert.DoesNotContain("Private original", safe);
        Assert.DoesNotContain("Private assigned", safe);
        Assert.DoesNotContain("Private endpoint label", safe);
        using var parsed = JsonDocument.Parse(safe);
        Assert.Equal("3.3.1.7", parsed.RootElement.GetProperty("driver_version").GetString());
        Assert.Equal(2, parsed.RootElement.GetProperty("channels").GetInt32());
    }
    [Theory]
    [InlineData("sessionKey")]
    [InlineData("privateKey")]
    [InlineData("private-key")]
    [InlineData("access_token")]
    [InlineData("accessToken")]
    [InlineData("pairing-pin")]
    public void ProtectsSecretLabelVariantsInJsonAndStderr(string label)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [label] = "synthetic-private-value" });
        using var document = JsonDocument.Parse(DiagnosticText.Protect(json));
        Assert.Equal("[redacted]", document.RootElement.GetProperty(label).GetString());
        Assert.DoesNotContain("synthetic-private-value", DiagnosticText.Protect(label + "=synthetic-private-value"));
    }
    [Fact]
    public void ExportOmitsFreeFormIdentityButKeepsStructuredFaultAndMetrics()
    {
        var diagnostics = new PlaybackDiagnostics(7,
            new EngineNotice("capture_unavailable", "192.0.2.1", "capture", "Synthetic Room ABCDEF123456 endpoint=synthetic.local"));
        var safe = DiagnosticText.ForExport(JsonSerializer.Serialize(diagnostics, AppSettings.JsonOptions));
        foreach (var identity in new[] { "Synthetic Room", "ABCDEF123456", "synthetic.local", "192.0.2.1" }) Assert.DoesNotContain(identity, safe);
        using var document = JsonDocument.Parse(safe);
        Assert.Equal(7, document.RootElement.GetProperty("reconnect_count").GetInt32());
        Assert.Equal("capture_unavailable", document.RootElement.GetProperty("last_fault").GetProperty("code").GetString());
    }
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
