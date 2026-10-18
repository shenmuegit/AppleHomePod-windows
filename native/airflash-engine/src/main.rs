//! Versioned JSONL qualification interface. stdout is exclusively structured IPC.
use airflash_engine::{
    rtsp::Cancellation,
    session::{ProbeOptions, probe_with_capture},
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    io::{self, BufRead, Write},
    thread::{self, JoinHandle},
};
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    version: u32,
    id: String,
    session_id: String,
    command: String,
    #[serde(default)]
    params: Value,
}
fn send(id: &str, session: &str, mut event: Value) {
    if event["event"] == "error" && event.get("code").is_none() {
        event["code"] = json!("command_error");
        event["host"] = Value::Null;
        event["channel"] = json!("ipc");
        event["retryable"] = json!(false);
    }
    event["version"] = json!(1);
    event["id"] = json!(id);
    event["session_id"] = json!(session);
    let mut stdout = io::stdout().lock();
    let _ = writeln!(stdout, "{event}");
    let _ = stdout.flush();
}
struct Running {
    id: String,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    pending_pin: Option<std::sync::mpsc::Sender<String>>,
    gain_limit: f64,
    volume: airflash_engine::volume::Control,
    equalizer: Option<(airflash_engine::equalizer::Control, u32)>,
    capture_id: Option<String>,
}
impl Drop for Running {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn main() {
    let mut running: Option<Running> = None;
    let mut armed: Option<std::sync::Arc<airflash_engine::live::Loopback>> = None;
    // Limit individual IPC commands; no unbounded read_line allocation.
    let mut input = io::stdin().lock();
    let mut bytes = Vec::new();
    loop {
        bytes.clear();
        let mut exceeded = false;
        loop {
            let available = match input.fill_buf() {
                Ok(b) => b,
                Err(_) => return,
            };
            if available.is_empty() {
                break;
            }
            let n = available
                .iter()
                .position(|b| *b == b'\n')
                .map_or(available.len(), |p| p + 1);
            let finished = available[n - 1] == b'\n';
            if bytes.len() + n > 65536 {
                exceeded = true;
            } else if !exceeded {
                bytes.extend_from_slice(&available[..n]);
            }
            input.consume(n);
            if finished {
                break;
            }
        }
        if bytes.is_empty() && !exceeded {
            break;
        }
        if exceeded {
            send(
                "",
                "",
                json!({"event":"error","message":"IPC command exceeds 64KiB"}),
            );
            continue;
        }
        let request: Request = match serde_json::from_slice(&bytes) {
            Ok(r) => r,
            Err(e) => {
                send("", "", json!({"event":"error","message":e.to_string()}));
                continue;
            }
        };
        if request.version != 1 {
            send(
                &request.id,
                &request.session_id,
                json!({"event":"error","message":"unsupported IPC version"}),
            );
            continue;
        }
        match request.command.as_str() {
            "hello" => send(
                &request.id,
                &request.session_id,
                json!({"event":"hello","engine_version":env!("CARGO_PKG_VERSION"),"mode":"native","qualification":"partial","live_loopback":true,"latency_negotiation":true,"commands":["hello","start","probe","stop","set_gain","set_equalizer","set_device_volume","pair","pair_pin","arm_capture","begin_capture","reset_capture","disarm_capture","set_capture_gate"],"production_ready":false}),
            ),
            "arm_capture" => {
                let result = (|| -> anyhow::Result<_> {
                    let options: airflash_engine::live::ArmCapture = serde_json::from_value(request.params)?;
                    options.validate(armed.is_some(), running.is_some())?;
                    let eq = airflash_engine::equalizer::Control::new(Default::default(), options.sample_rate)?;
                    airflash_engine::live::Loopback::arm(options.endpoint_id, options.sample_rate, eq, options.capture_id)
                })();
                match result {
                    Ok(source) => {
                        send(&request.id, &request.session_id, json!({"event":"capture_ready", "capture_id":source.capture_id, "epoch":source.epoch(), "session_instance_id":source.session_instance_id}));
                        armed = Some(std::sync::Arc::new(source));
                    }
                    Err(e) => send(&request.id, &request.session_id, json!({"event":"error", "message":format!("{e:#}")})),
                }
            }
            "begin_capture" | "reset_capture" | "set_capture_gate" | "disarm_capture" => {
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Key { capture_id: String, epoch: u64 }
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Gate { capture_id: String, epoch: u64, open: bool, clear_pending: bool }
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Disarm { capture_id: String }
                let result = (|| -> anyhow::Result<Value> {
                    let source = armed.as_ref().ok_or_else(|| anyhow::anyhow!("no armed capture"))?;
                    if request.command == "disarm_capture" {
                        let key: Disarm = serde_json::from_value(request.params)?;
                        source.validate_key(&key.capture_id, source.epoch())?;
                        anyhow::ensure!(running.as_ref().is_none_or(|r| r.capture_id.as_deref() != Some(&key.capture_id)), "stop transport before disarming capture");
                        return Ok(json!({"event":"capture_disarmed", "capture_id":key.capture_id}));
                    }
                    if request.command == "set_capture_gate" {
                        let gate: Gate = serde_json::from_value(request.params)?;
                        source.validate_key(&gate.capture_id, gate.epoch)?;
                        source.set_gate(gate.epoch, gate.open, gate.clear_pending)?;
                        return Ok(json!({"event":"capture_gate", "capture_id":gate.capture_id, "epoch":gate.epoch, "open":gate.open}));
                    }
                    let key: Key = serde_json::from_value(request.params)?;
                    source.validate_key(&key.capture_id, key.epoch)?;
                    if request.command == "begin_capture" { source.begin(key.epoch)?; }
                    else {
                        anyhow::ensure!(running.as_ref().is_none_or(|r| r.capture_id.as_deref() != Some(&key.capture_id)), "stop transport before resetting capture");
                        source.reset(key.epoch)?;
                    }
                    Ok(json!({"event":if request.command == "begin_capture" {"capture_begun"} else {"capture_reset"}, "capture_id":key.capture_id, "epoch":source.epoch()}))
                })();
                match result {
                    Ok(event) => {
                        if request.command == "disarm_capture" { drop(armed.take()); }
                        send(&request.id, &request.session_id, event);
                    }
                    Err(e) => send(&request.id, &request.session_id, json!({"event":"error", "message":format!("{e:#}")})),
                }
            }
            "probe" | "start" => {
                let options: ProbeOptions = match serde_json::from_value(request.params) {
                    Ok(o) => o,
                    Err(e) => {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":e.to_string()}),
                        );
                        continue;
                    }
                };
                if let Err(e) = if request.command == "start" {
                    options.validate_start()
                } else {
                    options.validate()
                } {
                    send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"error","message":e.to_string()}),
                    );
                    continue;
                }
                let prepared = match (|| -> anyhow::Result<_> {
                    if let Some(id) = &options.capture_id {
                        let source = armed.as_ref().ok_or_else(|| anyhow::anyhow!("no armed capture"))?;
                        source.validate_key(id, options.capture_epoch.unwrap())?;
                        source.ensure_begun()?;
                        anyhow::ensure!(options.sample_rate == source.sample_rate && options.capture_endpoint.as_ref().is_none_or(|e| Some(e) == source.endpoint_id.as_ref()), "capture endpoint or rate mismatch");
                        anyhow::ensure!(running.is_none(), "stop transport before starting a new capture session");
                        Ok(Some(source.clone()))
                    } else {
                        anyhow::ensure!(armed.is_none(), "legacy source cannot replace an armed capture");
                        Ok(None)
                    }
                })() {
                    Ok(source) => source,
                    Err(e) => {
                        send(&request.id, &request.session_id, json!({"event":"error", "message":format!("{e:#}")}));
                        continue;
                    }
                };
                if let Some(old) = running.take() {
                    drop(old);
                }
                let cancel = Cancellation::default();
                let worker_cancel = cancel.clone();
                let session = request.session_id.clone();
                let gain =
                    std::sync::Arc::new(std::sync::atomic::AtomicU32::new(options.gain.to_bits()));
                let worker_gain = gain.clone();
                let volume = airflash_engine::volume::Control::default();
                let worker_volume = volume.clone();
                let gain_limit = if request.command == "probe" { options.probe_gain_limit() } else { 1.0 };
                let rate = options.sample_rate;
                let equalizer = airflash_engine::equalizer::Control::new(options.equalizer, rate).unwrap();
                let worker_equalizer = equalizer.clone();
                let live_equalizer = (request.command == "start" && prepared.is_none()).then_some((equalizer, rate));
                let capture_id = options.capture_id.clone();
                let worker = thread::spawn(move || {
                    let emit = |e| send(&request.id, &request.session_id, e);
                    if let Err(error) = probe_with_capture(options, worker_cancel, worker_gain, worker_volume, worker_equalizer, prepared, emit) {
                        emit(airflash_engine::transport::error_event(&error));
                    }
                });
                running = Some(Running {
                    id: session,
                    cancel,
                    worker: Some(worker),
                    gain,
                    pending_pin: None,
                    gain_limit,
                    volume,
                    equalizer: live_equalizer,
                    capture_id,
                });
            }
            "set_device_volume" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id && r.pending_pin.is_none()) {
                    if let (Some(percent), Some(sequence)) = (
                        request.params.get("volume").and_then(Value::as_u64).filter(|v| *v <= 100),
                        request.params.get("sequence").and_then(Value::as_u64).filter(|v| *v > 0)) {
                        r.volume.set(airflash_engine::volume::Command { sequence, percent: percent as u8 });
                    } else {
                        send(&request.id, &request.session_id, json!({"event":"device_volume_error","message":"invalid device volume command"}));
                    }
                }
            }
            "set_equalizer" => {
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Update { sequence: u64, equalizer: airflash_engine::equalizer::Settings }
                let sequence = request.params.get("sequence").cloned().unwrap_or(Value::Null);
                let result = (|| -> anyhow::Result<_> {
                    let update: Update = serde_json::from_value(request.params)?;
                    anyhow::ensure!(update.sequence > 0, "equalizer sequence must be positive");
                    let r = running.as_ref().filter(|r| r.id == request.session_id).ok_or_else(|| anyhow::anyhow!("equalizer session is no longer active"))?;
                    let (control, rate) = r.equalizer.as_ref().ok_or_else(|| anyhow::anyhow!("equalizer updates require a live streaming session"))?;
                    let prepared = airflash_engine::equalizer::Prepared::new(update.equalizer, *rate)?;
                    control.set(update.sequence, prepared);
                    Ok(prepared)
                })();
                match result {
                    Ok(prepared) => send(&request.id, &request.session_id, json!({"event":"equalizer_changed","sequence":sequence,"auto_attenuation_db":prepared.auto_attenuation_db,"effective_preamp_db":prepared.effective_preamp_db})),
                    Err(error) => send(&request.id, &request.session_id, json!({"event":"equalizer_error","sequence":sequence,"message":error.to_string()})),
                }
            }
            "set_gain" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id) {
                    if let Some(g) = request
                        .params
                        .get("gain")
                        .and_then(Value::as_f64)
                        .filter(|g| g.is_finite() && (0.0..=r.gain_limit).contains(g))
                    {
                        r.gain
                            .store((g as f32).to_bits(), std::sync::atomic::Ordering::Release);
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"gain_changed","gain":g}),
                        );
                    } else {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":format!("gain must be 0..{}", r.gain_limit)}),
                        );
                    }
                }
            }
            "pair_pin" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id) {
                    if let (Some(channel), Some(pin)) = (
                        &r.pending_pin,
                        request.params.get("pin").and_then(Value::as_str),
                    ) {
                        let _ = channel.send(pin.to_owned());
                    }
                }
            }
            "pair" => {
                if armed.is_some() {
                    send(&request.id, &request.session_id, json!({"event":"error", "message":"disarm capture before pairing"}));
                    continue;
                }
                let peer: airflash_engine::session::Peer = match serde_json::from_value(
                    request.params.get("peer").cloned().unwrap_or(Value::Null),
                ) {
                    Ok(peer) => peer,
                    Err(e) => {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":e.to_string()}),
                        );
                        continue;
                    }
                };
                if let Some(old) = running.take() {
                    drop(old);
                }
                let cancel = Cancellation::default();
                let child_cancel = cancel.clone();
                let session = request.session_id.clone();
                let (tx, rx) = std::sync::mpsc::channel::<String>();
                let worker = thread::spawn(move || {
                    let result = (|| -> anyhow::Result<()> {
                        let mut conn = airflash_engine::rtsp::Connection::connect(
                            std::net::SocketAddr::new(peer.host, peer.port),
                            child_cancel.clone(),
                        )?;
                        let info = conn.request("GET", "/info", &[], &[])?.plist()?;
                        let device_id = info
                            .as_dictionary()
                            .and_then(|d| d.get("deviceID"))
                            .and_then(plist::Value::as_string)
                            .ok_or_else(|| anyhow::anyhow!("missing device identifier"))?
                            .to_owned();
                        let credentials = airflash_engine::auth::pair_prompt(&mut conn, || {
                            send(
                                &request.id,
                                &request.session_id,
                                json!({"event":"pin_required","host":peer.host}),
                            );
                            let deadline =
                                std::time::Instant::now() + std::time::Duration::from_secs(120);
                            loop {
                                child_cancel.check()?;
                                anyhow::ensure!(
                                    std::time::Instant::now() < deadline,
                                    "PIN entry timed out"
                                );
                                match rx.recv_timeout(std::time::Duration::from_millis(100)) {
                                    Ok(pin) => return Ok(pin),
                                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
                                    Err(_) => anyhow::bail!("PIN entry cancelled"),
                                }
                            }
                        })?;
                        airflash_engine::credentials::save(
                            &airflash_engine::credentials::directory(),
                            &device_id,
                            &credentials,
                        )?;
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"paired","device_id":device_id}),
                        );
                        Ok(())
                    })();
                    if let Err(error) = result {
                        send(
                            &request.id,
                            &request.session_id,
                            airflash_engine::transport::error_event(&error),
                        );
                    }
                });
                running = Some(Running {
                    id: session,
                    cancel,
                    worker: Some(worker),
                    gain: std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0)),
                    pending_pin: Some(tx),
                    gain_limit: 0.0,
                    volume: airflash_engine::volume::Control::default(),
                    equalizer: None,
                    capture_id: None,
                });
            }
            "stop" => {
                if running.as_ref().is_some_and(|r| r.id == request.session_id) {
                    let old = running.take().unwrap();
                    drop(old);
                }
                send(&request.id, &request.session_id, json!({"event":"stopped"}));
            }
            _ => send(
                &request.id,
                &request.session_id,
                json!({"event":"error","message":"unknown command"}),
            ),
        }
    }
    if let Some(old) = running {
        drop(old);
    }
}
