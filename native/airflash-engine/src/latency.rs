//! Receiver constraints, requested targets and cache windows are different quantities.
use anyhow::{Context, Result, ensure};
use plist::Value;
use serde::{Deserialize, Serialize};
use std::time::Duration;

const COMPATIBILITY_MIN_MS: u32 = 250;
const OFFER_CAPACITY_MS: u32 = 2000;
const MAX_TARGET_MS: u32 = 2000;
const MAX_REPORT_SECONDS: u64 = 10;

#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Policy {
    #[default]
    Fixed,
    Negotiate,
}

pub fn offer(preferred_ms: Option<u32>) -> (u32, u32) {
    preferred_ms.map_or((COMPATIBILITY_MIN_MS, OFFER_CAPACITY_MS), |ms| (ms, ms))
}

#[derive(Debug, Serialize)]
pub struct Negotiation {
    policy: &'static str,
    preferred_ms: Option<u32>,
    offered_min_ms: u32,
    offered_max_ms: u32,
    pub receiver_min_samples: Option<u32>,
    pub receiver_max_samples: Option<u32>,
    pub selected_ms: u32,
    source: &'static str,
    fallback_reason: Option<String>,
    arrival_to_render_ms: Option<f64>,
}
impl Negotiation {
    pub fn recovery_bounds(&self, rate: u32) -> (Option<Duration>, Option<Duration>) {
        let duration = |frames: Option<u32>| frames.filter(|n| *n > 0)
            .map(|n| Duration::from_nanos(u64::from(n) * 1_000_000_000 / u64::from(rate)));
        (duration(self.receiver_min_samples), duration(self.receiver_max_samples))
    }
}

fn bound(reply: &Value, key: &str, rate: u32) -> Result<Option<u32>> {
    let fields = reply.as_dictionary().context("receiver stream is not a dictionary")?;
    let Some(value) = fields.get(key) else { return Ok(None); };
    let samples = value.as_unsigned_integer().with_context(|| format!("invalid {key} type or sign"))?;
    ensure!(samples <= u64::from(rate) * MAX_REPORT_SECONDS, "{key} exceeds supported report size");
    Ok(Some(u32::try_from(samples)?))
}

pub fn select(preferred_ms: Option<u32>, rate: u32, reply: &Value) -> Result<Negotiation> {
    ensure!(crate::rtp::SUPPORTED_RATES.contains(&rate), "unsupported negotiation sample rate");
    ensure!(preferred_ms.is_none_or(|n| n <= MAX_TARGET_MS), "invalid verified timing preference");
    let (offered_min_ms, offered_max_ms) = offer(preferred_ms);
    let mut choice = Negotiation {
        policy: "negotiate", preferred_ms, offered_min_ms, offered_max_ms,
        receiver_min_samples: None, receiver_max_samples: None,
        selected_ms: preferred_ms.unwrap_or(COMPATIBILITY_MIN_MS),
        source: if preferred_ms.is_some() { "verified_reference" } else { "compatibility_fallback" },
        fallback_reason: None,
        arrival_to_render_ms: reply.as_dictionary().and_then(|d| d.get("arrivalToRenderLatencyMs"))
            .and_then(|v| v.as_unsigned_integer().map(|n| n as f64).or_else(|| v.as_real()))
            .filter(|n| n.is_finite() && (0.0..=10_000.0).contains(n)),
    };
    let bounds = (|| -> Result<_> {
        let min = bound(reply, "latencyMin", rate)?;
        let max = bound(reply, "latencyMax", rate)?;
        ensure!(!matches!((min.filter(|n| *n > 0), max.filter(|n| *n > 0)), (Some(a), Some(b)) if a > b), "receiver latencyMin exceeds latencyMax");
        Ok((min, max))
    })();
    let (min, max) = match bounds {
        Ok(bounds) => bounds,
        Err(error) if preferred_ms.is_some() => {
            choice.source = "verified_reference_invalid_reply";
            choice.fallback_reason = Some(format!("{error:#}"));
            return Ok(choice);
        }
        Err(error) => return Err(error.context("receiver timing is invalid and no verified reference exists")),
    };
    let min_ms = min.filter(|n| *n > 0).map(|n| (u64::from(n) * 1000).div_ceil(u64::from(rate)) as u32);
    let max_ms = max.filter(|n| *n > 0).map(|n| (u64::from(n) * 1000 / u64::from(rate)) as u32);
    let lower = min_ms.unwrap_or(0);
    let upper = max_ms.unwrap_or(MAX_TARGET_MS).min(MAX_TARGET_MS);
    ensure!(lower <= upper, "receiver window cannot be represented within supported target range");
    choice.receiver_min_samples = min;
    choice.receiver_max_samples = max;
    choice.selected_ms = preferred_ms.or(min_ms).unwrap_or(COMPATIBILITY_MIN_MS).clamp(lower, upper);
    choice.source = match (min_ms, max_ms) {
        (Some(_), Some(_)) => "receiver_window",
        (Some(_), None) => "receiver_minimum",
        (None, Some(_)) => "receiver_maximum",
        _ => choice.source,
    };
    Ok(choice)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn number(n: u64) -> Value { Value::Integer(n.into()) }
    fn reply(fields: Vec<(&str, Value)>) -> Value {
        Value::Dictionary(fields.into_iter().map(|(k, v)| (k.to_string(), v)).collect())
    }
    #[test]
    fn each_device_uses_its_own_reference_or_response_and_never_a_two_second_target() {
        for (reference, fields, selected, source) in [
            (Some(100), vec![], 100, "verified_reference"),
            (None, vec![], 250, "compatibility_fallback"),
            (None, vec![("latencyMin", number(4410)), ("latencyMax", number(22050))], 100, "receiver_window"),
            (Some(100), vec![("latencyMin", number(8820)), ("latencyMax", number(35280))], 200, "receiver_window"),
            (Some(100), vec![("latencyMin", number(441)), ("latencyMax", number(2205))], 50, "receiver_window"),
            (None, vec![("latencyMin", number(8820))], 200, "receiver_minimum"),
            (None, vec![("latencyMax", number(2205))], 50, "receiver_maximum"),
            (Some(100), vec![("latencyMin", number(0)), ("latencyMax", number(0))], 100, "verified_reference"),
            (Some(100), vec![("arrivalToRenderLatencyMs", number(70))], 100, "verified_reference"),
            (None, vec![("arrivalToRenderLatencyMs", number(70))], 250, "compatibility_fallback"),
        ] {
            let value = select(reference, 44100, &reply(fields)).unwrap();
            assert_eq!((value.selected_ms, value.source), (selected, source));
            assert_eq!(value.preferred_ms, reference);
        }
        assert_eq!(select(None, 44100, &reply(vec![])).unwrap().selected_ms, 250);
        assert_eq!(select(Some(75), 44100, &reply(vec![])).unwrap().selected_ms, 75);
    }
    #[test]
    fn fractional_frame_bounds_round_inwards_and_keep_sample_units() {
        let value = select(None, 44100, &reply(vec![("latencyMin", number(442)), ("latencyMax", number(500))])).unwrap();
        assert_eq!(value.selected_ms, 11);
        let (min, max) = value.recovery_bounds(44100);
        assert_eq!(min, Some(Duration::from_nanos(442_000_000_000 / 44100)));
        assert_eq!(max, Some(Duration::from_nanos(500_000_000_000 / 44100)));
        assert!(select(None, 44100, &reply(vec![("latencyMin", number(442)), ("latencyMax", number(450))])).is_err());
        assert_eq!(select(None, 48000, &reply(vec![("latencyMin", number(481))])).unwrap().selected_ms, 11);
    }
    #[test]
    fn invalid_reports_can_only_fall_back_to_this_devices_verified_reference() {
        for fields in [
            vec![("latencyMin", number(8820)), ("latencyMax", number(4410))],
            vec![("latencyMin", Value::String("100".into()))],
            vec![("latencyMin", Value::Integer((-1i64).into()))],
            vec![("latencyMax", number(u64::MAX))],
        ] {
            let response = reply(fields);
            assert!(select(None, 44100, &response).is_err());
            let known = select(Some(100), 44100, &response).unwrap();
            assert_eq!((known.selected_ms, known.source), (100, "verified_reference_invalid_reply"));
            assert!(known.fallback_reason.is_some());
            assert_eq!(known.recovery_bounds(44100), (None, None));
        }
    }
    #[test]
    fn unsupported_real_minimum_is_not_silently_clamped_below_the_device() {
        let response = reply(vec![("latencyMin", number(132300)), ("latencyMax", number(176400))]);
        assert!(select(Some(100), 44100, &response).is_err());
        assert!(select(None, 44100, &response).is_err());
        assert!(select(None, 0, &reply(vec![])).is_err());
    }
}
