using System.Text.Json;
namespace AirFlash.Core;

public sealed record LatencyNegotiation(string Host, string Policy, int? PreferredMs, int OfferedMinMs, int OfferedMaxMs,
    uint? ReceiverMinSamples, uint? ReceiverMaxSamples, int SelectedMs, string Source, string? FallbackReason, double? ArrivalToRenderMs,
    uint SampleRate)
{
    public static LatencyNegotiation? Parse(JsonElement item, int? preference)
    {
        if (item.Integer("sample_rate") is not { } rate || rate is <= 0 or > uint.MaxValue
            || !item.TryGetProperty("latency_negotiation", out var data) || data.ValueKind != JsonValueKind.Object
            || data.Text("policy") != "negotiate"
            || !NullableInteger(data, "preferred_ms", 2000, out var preferred) || preferred != preference
            || data.Integer("offered_min_ms") is not { } min || min is < 0 or > 2000
            || data.Integer("offered_max_ms") is not { } max || max is < 0 or > 2000 || min > max
            || data.Integer("selected_ms") is not { } selected || selected is < 0 or > 2000
            || !NullableInteger(data, "receiver_min_samples", uint.MaxValue, out var receiverMin)
            || !NullableInteger(data, "receiver_max_samples", uint.MaxValue, out var receiverMax)
            || data.Text("source") is not ("receiver_window" or "receiver_minimum" or "receiver_maximum" or "verified_reference"
                or "compatibility_fallback" or "verified_reference_invalid_reply")
            || !data.TryGetProperty("fallback_reason", out var reason) || reason.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
            || !data.TryGetProperty("arrival_to_render_ms", out var render)
            || render.ValueKind != JsonValueKind.Null && (render.ValueKind != JsonValueKind.Number
                || !render.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0)) return null;
        var source = data.Text("source");
        if (source is "receiver_window" or "receiver_minimum" or "receiver_maximum")
        {
            var lower = receiverMin is > 0 ? (receiverMin.Value * 1000 + rate - 1) / rate : (long?)null;
            var upper = receiverMax is > 0 ? receiverMax.Value * 1000 / rate : (long?)null;
            var boundsMatch = source switch
            {
                "receiver_window" => lower is not null && upper is not null,
                "receiver_minimum" => lower is not null && upper is null,
                _ => lower is null && upper is not null
            };
            if (!boundsMatch || selected < lower || selected > upper) return null;
        }
        else if (selected < min || selected > max) return null;
        return new(item.Text("host"), "negotiate", (int?)preferred, (int)min, (int)max, (uint?)receiverMin, (uint?)receiverMax,
            (int)selected, source, reason.ValueKind == JsonValueKind.Null ? null : reason.GetString(),
            render.ValueKind == JsonValueKind.Null ? null : render.GetDouble(), (uint)rate);
    }
    private static bool NullableInteger(JsonElement data, string name, long max, out long? number)
    {
        number = data.Integer(name);
        return data.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.Null || number is >= 0 && number <= max);
    }
}
