using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LatencyNegotiationTests
{
    private static System.Text.Json.Nodes.JsonObject Event(int? min, int? max, int selected, string source) =>
        JsonSerializer.SerializeToNode(new { host = "127.0.0.1", sample_rate = 44100,
            latency_negotiation = new { policy = "negotiate", preferred_ms = 100, offered_min_ms = 100, offered_max_ms = 100,
                receiver_min_samples = min, receiver_max_samples = max, selected_ms = selected, source,
                fallback_reason = (string?)null, arrival_to_render_ms = (double?)70 } })!.AsObject();
    [Theory]
    [InlineData(8820, 35280, 200, "receiver_window", true)]
    [InlineData(8821, 35280, 200, "receiver_window", false)]
    [InlineData(8821, 35280, 201, "receiver_window", true)]
    [InlineData(8820, 35279, 800, "receiver_window", false)]
    [InlineData(8820, 35279, 799, "receiver_window", true)]
    [InlineData(8820, 0, 200, "receiver_minimum", true)]
    [InlineData(8820, null, 200, "receiver_minimum", true)]
    [InlineData(0, 35279, 799, "receiver_maximum", true)]
    [InlineData(null, 35279, 800, "receiver_maximum", false)]
    [InlineData(null, 35279, 799, "receiver_maximum", true)]
    [InlineData(8820, 0, 199, "receiver_minimum", false)]
    [InlineData(0, 0, 200, "receiver_window", false)]
    [InlineData(null, null, 200, "receiver_minimum", false)]
    [InlineData(35280, 8820, 200, "receiver_window", false)]
    [InlineData(null, null, 200, "verified_reference", false)]
    [InlineData(null, null, 200, "compatibility_fallback", false)]
    [InlineData(null, null, 200, "verified_reference_invalid_reply", false)]
    [InlineData(null, null, 100, "verified_reference_invalid_reply", true)]
    public void ReceiverBoundsOverrideOfferButFallbackCannot(int? min, int? max, int selected, string source, bool accepted)
    {
        var item = JsonSerializer.SerializeToElement(Event(min, max, selected, source));
        var parsed = LatencyNegotiation.Parse(item, 100);
        Assert.Equal(accepted, parsed is not null);
        if (parsed is not null) Assert.Equal(selected, parsed.SelectedMs);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("string")]
    [InlineData("overflow")]
    public void InvalidSampleRateCannotAuthorizeReceiverBounds(string invalid)
    {
        var item = Event(8820, 35280, 100, "receiver_window");
        if (invalid == "missing") item.Remove("sample_rate");
        else if (invalid == "string") item["sample_rate"] = "44100";
        else item["sample_rate"] = invalid switch { "zero" => 0L, "negative" => -1L, _ => 4294967296L };
        Assert.Null(LatencyNegotiation.Parse(JsonSerializer.SerializeToElement(item), 100));
    }
}
