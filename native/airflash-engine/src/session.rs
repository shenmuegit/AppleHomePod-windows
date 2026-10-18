//! AirPlay member orchestration, live loopback and finite quiet qualification.
use crate::{
    auth,
    clock::{Clock, PtpMaster},
    crypto::derive,
    rtp::{self, FRAMES, PCM_BYTES, Packetizer},
    rtsp::{Cancellation, Connection},
    transport::{
        EventWorker, Fault, FeedbackTiming, FeedbackWorker, Health, MediaSchedule, MediaTransport,
    },
};
use anyhow::{Context, Result, ensure};
use plist::{Dictionary, Value};
use serde::Deserialize;
use serde_json::{Value as Json, json};
use std::{
    net::{IpAddr, SocketAddr, UdpSocket},
    path::PathBuf,
    sync::atomic::Ordering,
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use uuid::Uuid;

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Peer {
    pub host: IpAddr,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub codecs: Vec<u8>,
    #[serde(default)]
    pub follow_receiver_clock: bool,
}
fn default_port() -> u16 {
    7000
}
fn default_sample_rate() -> u32 {
    44100
}
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProbeOptions {
    pub peers: Vec<Peer>,
    #[serde(default)]
    pub wav_path: PathBuf,
    #[serde(default = "default_source")]
    pub source: String,
    #[serde(default)]
    pub capture_endpoint: Option<String>,
    #[serde(default)]
    pub capture_id: Option<String>,
    #[serde(default)]
    pub capture_epoch: Option<u64>,
    #[serde(default)]
    pub await_volume_gate: Option<bool>,
    pub duration_ms: u32,
    #[serde(default)]
    pub latency_ms: Option<u32>,
    #[serde(default)]
    pub latency_max_ms: Option<u32>,
    pub gain: f32,
    #[serde(default)]
    pub equalizer: crate::equalizer::Settings,
    #[serde(default = "default_sample_rate")]
    pub sample_rate: u32,
    #[serde(default = "default_timing")]
    pub timing: String,
    #[serde(default)]
    pub group_id: Option<String>,
    #[serde(default)]
    pub handshake_only: bool,
    #[serde(default)]
    pub record_mic_path: Option<PathBuf>,
}
fn default_source() -> String {
    "wav".into()
}
fn default_timing() -> String {
    "ptp".into()
}
impl ProbeOptions {
    pub fn probe_gain_limit(&self) -> f64 {
        if self.source == "wav" { 1.0 } else { 0.1 }
    }
    pub fn validate_start(&self) -> Result<()> {
        self.validate_capture_fields()?;
        ensure!(
            self.source == "loopback" && self.duration_ms == 0,
            "start requires unbounded loopback source"
        );
        ensure!(
            self.gain.is_finite() && (0.0..=1.0).contains(&self.gain),
            "gain must be 0..1"
        );
        let mut bounded = self.clone();
        bounded.duration_ms = 5000;
        bounded.gain = 0.1;
        bounded.capture_id = None;
        bounded.capture_epoch = None;
        bounded.await_volume_gate = None;
        self.equalizer.validate()?;
        bounded.equalizer = crate::equalizer::Settings::default();
        bounded.validate()
    }
    pub fn validate(&self) -> Result<()> {
        ensure!(self.capture_id.is_none() && self.capture_epoch.is_none() && self.await_volume_gate.is_none(), "prepared capture is only available to start");
        self.equalizer.validate()?;
        ensure!(!self.equalizer.enabled, "finite probes require the equalizer to be disabled");
        ensure!(
            self.source == "wav" || self.source == "loopback",
            "unknown audio source"
        );
        ensure!(
            !self.peers.is_empty() && self.peers.len() <= 2,
            "probe supports one device or one stereo pair"
        );
        ensure!(
            self.peers.iter().all(|p| p.host.is_ipv4() && p.port > 0),
            "IPv4 endpoint required"
        );
        if self.peers.len() == 2 {
            ensure!(self.peers[0].host != self.peers[1].host, "duplicate peer");
        }
        ensure!(
            (1..=5000).contains(&self.duration_ms),
            "probe duration must be 1..5000 ms"
        );
        ensure!(
            self.latency_ms.is_none_or(|ms| ms <= 2000),
            "latency must be 0..2000 ms"
        );
        ensure!(self.latency_ms.is_some() || self.timing == "ntp", "PTP requires an explicit latency");
        ensure!(self.latency_max_ms.is_none_or(|max| max <= 2000 && self.latency_ms.is_some_and(|min| min <= max)), "latency maximum requires a valid minimum and must be within 0..2000 ms");
        ensure!(
            self.gain.is_finite() && (0.0..=self.probe_gain_limit() as f32).contains(&self.gain),
            "probe gain must be 0..{}", self.probe_gain_limit()
        );
        ensure!(
            self.timing == "ptp" || self.timing == "ntp",
            "timing must be ptp or ntp"
        );
        ensure!(
            crate::rtp::SUPPORTED_RATES.contains(&self.sample_rate),
            "sample rate must be 44100 or 48000"
        );
        Ok(())
    }
    fn validate_capture_fields(&self) -> Result<()> {
        if self.capture_id.is_none() && self.capture_epoch.is_none() && self.await_volume_gate.is_none() { return Ok(()); }
        let id = self.capture_id.as_deref().context("capture_id required")?;
        Uuid::parse_str(id).context("capture_id must be a UUID")?;
        ensure!(self.capture_epoch.is_some_and(|e| e > 0) && self.await_volume_gate == Some(true), "positive capture_epoch and await_volume_gate=true required");
        ensure!(self.sample_rate == 44100 && !self.equalizer.enabled, "prepared capture requires 44100 Hz and neutral EQ");
        Ok(())
    }
}
fn dictionary(items: Vec<(&str, Value)>) -> Value {
    Value::Dictionary(
        items
            .into_iter()
            .map(|(k, v)| (k.to_owned(), v))
            .collect::<Dictionary>(),
    )
}
fn string(s: impl Into<String>) -> Value {
    Value::String(s.into())
}
fn number(n: u64) -> Value {
    Value::Integer(n.into())
}
fn fallback_latency(options: &ProbeOptions) -> Option<Duration> {
    options.latency_ms.filter(|min| options.latency_max_ms.is_none_or(|max| max == *min))
        .map(|ms| Duration::from_millis(u64::from(ms)))
}
fn audio_stream(options: &ProbeOptions, control_port: u16, key: &[u8], ssrc: u32, alac: bool) -> Value {
    let mut fields = vec![
        ("audioFormat", number(crate::rtp::audio_format(alac, options.sample_rate))),
        ("audioMode", string("default")),
        ("controlPort", number(control_port as u64)),
        ("ct", number(if alac { 2 } else { 1 })),
        ("isMedia", Value::Boolean(true)),
        ("shk", Value::Data(key.to_vec())),
        ("spf", number(FRAMES as u64)),
        ("sr", number(options.sample_rate as u64)),
        ("type", number(96)),
        ("supportsDynamicStreamID", Value::Boolean(false)),
        ("streamConnectionID", number(ssrc as u64)),
    ];
    if let Some(ms) = options.latency_ms {
        let samples = u64::from(ms) * u64::from(options.sample_rate) / 1000;
        let maximum = u64::from(options.latency_max_ms.unwrap_or(ms)) * u64::from(options.sample_rate) / 1000;
        fields.extend([("latencyMin", number(samples)), ("latencyMax", number(maximum))]);
    }
    dictionary(fields)
}
fn get_port(value: &Value, key: &str) -> Result<u16> {
    let p = value
        .as_dictionary()
        .and_then(|d| d.get(key))
        .and_then(Value::as_unsigned_integer)
        .context(format!("missing {key}"))?;
    ensure!(p > 0 && p <= 65535, "invalid {key}");
    Ok(p as u16)
}

struct NtpServer {
    port: u16,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
}
impl NtpServer {
    fn start(peers: Vec<IpAddr>, clock: Clock) -> Result<Self> {
        let sock = UdpSocket::bind("0.0.0.0:0")?;
        let port = sock.local_addr()?.port();
        sock.set_read_timeout(Some(Duration::from_millis(100)))?;
        let cancel = Cancellation::default();
        let stop = cancel.clone();
        let worker = thread::spawn(move || {
            let mut buf = [0; 128];
            while !stop.is_cancelled() {
                if let Ok((n, addr)) = sock.recv_from(&mut buf) {
                    if n != 32 || !peers.contains(&addr.ip()) || buf[1] & 0x7f != 0x52 {
                        continue;
                    }
                    let received = clock.now_ns();
                    let mut out = vec![0x80, 0xd3, 0, 7, 0, 0, 0, 0];
                    out.extend_from_slice(&buf[24..32]);
                    out.extend(rtp::ntp(received));
                    out.extend(rtp::ntp(clock.now_ns()));
                    let _ = sock.send_to(&out, addr);
                }
            }
        });
        Ok(Self {
            port,
            cancel,
            worker: Some(worker),
        })
    }
}
impl Drop for NtpServer {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct Member {
    peer: Peer,
    conn: std::sync::Arc<std::sync::Mutex<Connection>>,
    events: Option<EventWorker>,
    feedback: Option<FeedbackWorker>,
    health: Health,
    media: MediaTransport,
    uri: String,
    remote_control: u16,
    ready: bool,
    closed: bool,
    session_started: bool,
}
impl Member {
    fn connect(
        peer: &Peer,
        options: &ProbeOptions,
        clock_id: Option<u64>,
        ntp_port: u16,
        initial_rtp: u32,
        cancel: Cancellation,
        emit: &dyn Fn(Json),
    ) -> Result<Self> {
        let mut conn = Connection::connect(SocketAddr::new(peer.host, peer.port), cancel)?;
        let local = conn.local_addr()?.ip();
        let ssrc = rand::random::<u32>();
        let uri = format!("rtsp://{local}/{ssrc}");
        emit(json!({"event":"phase","host":peer.host,"phase":"info"}));
        let info = conn.request("GET", "/info", &[], &[])?.plist()?;
        let capabilities = info.as_dictionary().context("info is not a dictionary")?;
        emit(
            json!({"event":"peer_info","host":peer.host,"model":capabilities.get("model").and_then(Value::as_string),"source_version":capabilities.get("sourceVersion").and_then(Value::as_string)}),
        );
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticate"}));
        let device_id = capabilities
            .get("deviceID")
            .and_then(Value::as_string)
            .unwrap_or("");
        let saved = if device_id.is_empty() {
            None
        } else {
            crate::credentials::load(&crate::credentials::directory(), device_id)?
        };
        let secret = (if let Some(credentials) = saved {
            auth::verify(&mut conn, &credentials)
        } else {
            auth::transient(&mut conn)
        })
        .map_err(|error| {
            let mut fault = Fault::from_error(peer.host, "authentication", &error);
            if fault.code == "protocol_error" {
                fault.code = "authentication_failed".into();
            }
            fault
        })?;
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticated"}));
        let control = UdpSocket::bind(SocketAddr::new(local, 0))?;
        control.set_nonblocking(true)?;
        let audio = UdpSocket::bind(SocketAddr::new(local, 0))?;
        let audio_key = derive(&secret, "Events-Salt", "Events-Write-Encryption-Key");
        let rate = options.sample_rate;
        let mut packetizer = Packetizer::with_rate(audio_key, rand::random(), initial_rtp, if clock_id.is_some() { 0 } else { ssrc }, rate);
        let use_alac =
            !peer.codecs.is_empty() && !peer.codecs.contains(&0) && peer.codecs.contains(&1);
        ensure!(
            peer.codecs.is_empty() || peer.codecs.contains(&0) || use_alac,
            "receiver supports neither PCM nor ALAC"
        );
        if use_alac {
            packetizer.enable_alac();
        }
        let mut member = Self {
            peer: peer.clone(),
            conn: std::sync::Arc::new(std::sync::Mutex::new(conn)),
            events: None,
            feedback: None,
            health: Health::default(),
            media: MediaTransport::new(packetizer, audio, control, SocketAddr::new(peer.host, 0))?,
            uri,
            remote_control: 0,
            ready: false,
            closed: false,
            session_started: false,
        };
        // RAII owns the connection before SETUP so partial handshakes are torn down too.
        let sender_id = "02:57:32:41:50:01";
        let mut setup = vec![
            ("deviceID", string(sender_id)),
            ("macAddress", string(sender_id)),
            ("name", string("AppleHome")),
            ("sessionUUID", string(Uuid::new_v4().to_string())),
            ("timingProtocol", string(options.timing.to_uppercase())),
            ("isMultiSelectAirPlay", Value::Boolean(true)),
            ("groupContainsGroupLeader", Value::Boolean(false)),
            ("senderSupportsRelay", Value::Boolean(false)),
        ];
        if let Some(group) = &options.group_id {
            setup.push(("groupUUID", string(group)));
        }
        if let Some(id) = clock_id {
            let peer_info = dictionary(vec![
                ("ID", string(Uuid::new_v4().to_string())),
                ("DeviceType", number(0)),
                ("ClockID", number(id)),
                ("SupportsClockPortMatchingOverride", Value::Boolean(false)),
                ("Addresses", Value::Array(vec![string(local.to_string())])),
            ]);
            setup.push(("timingPeerInfo", peer_info.clone()));
            setup.push(("timingPeerList", Value::Array(vec![peer_info])));
        } else {
            setup.push(("timingPort", number(ntp_port as u64)));
        }
        emit(
            json!({"event":"phase","host":peer.host,"phase":"setup_session","timing":options.timing}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request("SETUP", &member.uri, &dictionary(setup))?
            .plist()?;
        member.session_started = true;
        let event_port = get_port(&response, "eventPort")?;
        member.events = Some(EventWorker::connect(
            SocketAddr::new(peer.host, event_port),
            &secret,
            member.health.clone(),
        )?);
        if clock_id.is_some() {
            let mut peers: Vec<Value> = options
                .peers
                .iter()
                .map(|p| string(p.host.to_string()))
                .collect();
            peers.push(string(local.to_string()));
            member.conn.lock().unwrap().plist_request(
                "SETPEERS",
                &member.uri,
                &Value::Array(peers),
            )?;
        }
        let stream = audio_stream(options, member.media.control_port()?, &audio_key, ssrc, use_alac);
        emit(
            json!({"event":"phase","host":peer.host,"phase":"setup_audio","requested_latency_ms":options.latency_ms}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request(
                "SETUP",
                &member.uri,
                &dictionary(vec![("streams", Value::Array(vec![stream]))]),
            )?
            .plist()?;
        let stream = response
            .as_dictionary()
            .and_then(|d| d.get("streams"))
            .and_then(Value::as_array)
            .and_then(|a| a.first())
            .context("missing stream response")?;
        member.remote_control = get_port(stream, "controlPort")?;
        let data_port = get_port(stream, "dataPort")?;
        member.media.set_control_port(member.remote_control);
        member
            .media
            .connect_audio(SocketAddr::new(peer.host, data_port))?;
        let receiver_latency = stream
            .as_dictionary()
            .and_then(|d| d.get("latencyMin"))
            .and_then(Value::as_unsigned_integer);
        let effective_latency = receiver_latency
            .filter(|v| *v > 0 && *v <= rate as u64 * 10)
            .map(|samples| Duration::from_nanos(samples * 1_000_000_000 / rate as u64));
        if let Some(latency) = effective_latency.or_else(|| fallback_latency(options)) {
            member.media.configure_latency(latency, effective_latency.is_none());
        }
        emit(
            json!({"event":"negotiated","host":peer.host,"data_port":data_port,"codec":if use_alac{"alac"}else{"pcm"},"sample_rate":rate,"control_port":member.remote_control,"requested_latency_ms":options.latency_ms,"receiver_latency_min_samples":receiver_latency,"receiver_latency_max_samples":stream.as_dictionary().and_then(|d|d.get("latencyMax")).and_then(Value::as_unsigned_integer),"arrival_to_render_latency_ms":stream.as_dictionary().and_then(|d|d.get("arrivalToRenderLatencyMs")).and_then(Value::as_unsigned_integer),"measured_latency_ms":null}),
        );
        Ok(member)
    }
    fn record(&mut self) -> Result<()> {
        let headers = [
            ("Range", "npt=0-".into()),
            (
                "RTP-Info",
                format!(
                    "seq={};rtptime={}",
                    self.media.packetizer.seq, self.media.packetizer.timestamp
                ),
            ),
        ];
        self.conn
            .lock()
            .unwrap()
            .request("RECORD", &self.uri, &headers, &[])?;
        self.conn
            .lock()
            .unwrap()
            .request("FLUSH", &self.uri, &headers, &[])?;
        self.ready = true;
        Ok(())
    }
    fn start_feedback(&mut self, now: Instant) -> Result<()> {
        self.media.reset_watchdog(now);
        self.feedback = Some(FeedbackWorker::start(
            self.conn.clone(),
            self.peer.host.to_string(),
            self.health.clone(),
            FeedbackTiming::default(),
        )?);
        Ok(())
    }
    fn metrics(&self) -> Json {
        json!({"host":self.peer.host, "media":self.media.metrics(), "health":self.health.snapshot()})
    }
    fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.feedback.take();
        if self.session_started {
            let _ = self.conn.lock().unwrap().finish_session(&self.uri);
        }
        self.events.take();
        self.ready = false;
    }
}
impl Drop for Member {
    fn drop(&mut self) {
        self.close();
    }
}

fn load_quiet_wav(options: &ProbeOptions) -> Result<Vec<i16>> {
    let rate = options.sample_rate;
    let mut reader = hound::WavReader::open(&options.wav_path).context("open probe WAV")?;
    let spec = reader.spec();
    ensure!(
        spec.channels == 2
            && spec.sample_rate == rate
            && spec.bits_per_sample == 16
            && spec.sample_format == hound::SampleFormat::Int,
        "probe WAV must be S16 stereo {rate} Hz"
    );
    ensure!(
        u64::from(reader.len()) <= rate as u64 * 2 * 5,
        "probe WAV exceeds five seconds"
    );
    let samples = reader
        .samples::<i16>()
        .collect::<std::result::Result<Vec<_>, _>>()?;
    ensure!(
        !samples.is_empty() && samples.iter().all(|s| (*s as i32).abs() <= 1639),
        "probe WAV must have peak amplitude <= 0.05"
    );
    Ok(samples
        .into_iter()
        .map(|s| (s as f32 * options.gain).round() as i16)
        .collect())
}

pub fn probe(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    emit: impl Fn(Json),
) -> Result<()> {
    probe_with_volume(options, cancel, gain, crate::volume::Control::default(), emit)
}

pub fn probe_with_volume(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    let equalizer = crate::equalizer::Control::new(options.equalizer, options.sample_rate)?;
    probe_with_controls(options, cancel, gain, volume, equalizer, emit)
}

pub fn probe_with_controls(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    probe_with_capture(options, cancel, gain, volume, equalizer, None, emit)
}

pub fn probe_with_capture(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    prepared: Option<std::sync::Arc<crate::live::Loopback>>,
    emit: impl Fn(Json),
) -> Result<()> {
    if options.duration_ms == 0 {
        options.validate_start()?;
    } else {
        options.validate()?;
    }
    if let Some(source) = &prepared {
        source.validate_key(options.capture_id.as_deref().context("capture_id required")?, options.capture_epoch.context("capture_epoch required")?)?;
        source.ensure_begun()?;
        ensure!(source.sample_rate == options.sample_rate && options.capture_endpoint.as_ref().is_none_or(|e| Some(e) == source.endpoint_id.as_ref()), "capture endpoint or rate does not match armed source");
    } else {
        ensure!(options.capture_id.is_none(), "prepared capture unavailable");
    }
    let samples = if options.source == "wav" {
        load_quiet_wav(&options)?
    } else {
        Vec::new()
    };
    #[cfg(windows)]
    let microphone = options
        .record_mic_path
        .clone()
        .map(crate::wasapi::Microphone::start)
        .transpose()?;
    #[cfg(windows)]
    let _priority = crate::wasapi::Mmcss::new();
    let clock = Clock::new();
    let peers: Vec<_> = options.peers.iter().map(|p| p.host).collect();
    let ntp = NtpServer::start(peers.clone(), clock.clone())?;
    let ptp = if options.timing == "ptp" {
        Some(PtpMaster::start(peers, options.peers.iter().filter(|p| p.follow_receiver_clock).map(|p| p.host).collect(), clock.clone())?)
    } else {
        None
    };
    let clock_id = ptp.as_ref().map(|p| p.clock_id);
    let initial_rtp = rand::random::<u32>();
    let mut members = Vec::new();
    for peer in &options.peers {
        cancel.check()?;
        members.push(
            Member::connect(
                peer,
                &options,
                clock_id,
                ntp.port,
                initial_rtp,
                cancel.clone(),
                &emit,
            )
            .map_err(|e| Fault::from_error(peer.host, "setup", &e))?,
        );
    }
    if options.handshake_only {
        emit(
            json!({"event":"handshake_complete","members":members.len(),"audio_sent":false,"qualified":false}),
        );
        return Ok(());
    }
    for member in &mut members {
        cancel.check()?;
        member
            .record()
            .with_context(|| format!("RECORD {}", member.peer.host))
            .map_err(|error| Fault::from_error(member.peer.host, "control", &error))?;
    }
    if let Some(ptp) = &ptp {
        let until = Instant::now() + Duration::from_secs(2);
        for peer in options.peers.iter().filter(|p| p.follow_receiver_clock) {
            while ptp.timeline(peer.host, clock.now_ns()).is_none() {
                cancel.check()?;
                ensure!(Instant::now() < until, "Receiver PTP clock did not become ready");
                thread::sleep(Duration::from_millis(10));
            }
            emit(json!({"event":"ptp_following","host":peer.host,"clock_id":ptp.timeline(peer.host, clock.now_ns()).unwrap().0}));
        }
    }
    let rate = options.sample_rate;
    let is_prepared = prepared.is_some();
    let live = if let Some(source) = prepared { Some(source) } else if options.source == "loopback" {
        Some(std::sync::Arc::new(crate::live::Loopback::start(
            options.capture_endpoint.clone(),
            rate,
            equalizer,
        )?))
    } else {
        None
    };
    if let Some(source) = live.as_ref().filter(|_| !is_prepared) {
        let until = Instant::now() + Duration::from_millis(200);
        while !source.ready()? && Instant::now() < until {
            cancel.check()?;
            thread::sleep(Duration::from_millis(2));
        }
    }
    let start = Instant::now();
    let duration = Duration::from_millis(options.duration_ms as u64);
    let latency = options.latency_ms.unwrap_or(0) * rate / 1000;
    let mut schedule = MediaSchedule::new(start, rate);
    let mut packets = 0u64;
    let mut first_send_reported = false;
    for member in &mut members {
        member.start_feedback(start)?;
    }
    let volume_worker = crate::volume::Worker::start(members.iter().map(|m|
        (m.peer.host.to_string(), m.uri.clone(), m.conn.clone())).collect(), volume)?;
    let mut next_metrics = start + Duration::from_secs(1);
    let mut last_marker = 0u64;
    let mut next_sync = start;
    emit(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":null,"first_send_qpc_ns":null,"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
    );
    let mut pcm = vec![0; PCM_BYTES];
    let outcome = (|| -> Result<()> {
        while (options.duration_ms == 0 || start.elapsed() < duration) && !cancel.is_cancelled() {
            for event in volume_worker.events.try_iter() { emit(event); }
            for member in &members {
                for notice in member.health.notices() {
                    emit(notice);
                }
                member.health.check()?;
            }
            if is_prepared && !live.as_ref().unwrap().gate_open(options.capture_epoch.unwrap())? {
                let now = Instant::now();
                if now >= next_sync {
                    for m in &mut members {
                        let (id, ns) = if let Some(ptp) = &ptp {
                            let (id, ns) = ptp.timeline(m.peer.host, clock.now_ns()).context("Receiver PTP clock became stale")?;
                            (Some(id), ns)
                        } else { (None, clock.now_ns()) };
                        m.media.sync(ns, id, latency, packets == 0);
                    }
                    next_sync = now + Duration::from_millis(100);
                }
                thread::sleep(Duration::from_millis(2));
                continue;
            }
            if is_prepared && packets == 0 {
                schedule.rebase_before_first_send(Instant::now())?;
                next_sync = Instant::now();
            }
            let deadline = schedule.deadline();
            let now = Instant::now();
            if now < deadline {
                thread::sleep((deadline - now).min(Duration::from_millis(2)));
                continue;
            }
            let skipped = schedule.recover(now, options.duration_ms == 0)?;
            if skipped > 0 {
                for member in &mut members {
                    member.media.packetizer.skip_packets(skipped);
                }
                if let Some(source) = &live {
                    source.discard_stale();
                }
                next_sync = now;
                emit(
                    json!({"event":"warning","code":"sender_late_recovered","channel":"scheduler","host":null,"recovered":true,"skipped_packets":skipped,"message":"Sender discarded expired audio slots and recovered its timeline"}),
                );
            }
            if now >= next_sync {
                for m in &mut members {
                    let (id, ns) = if let Some(ptp) = &ptp {
                        let (id, ns) = ptp.timeline(m.peer.host, clock.now_ns()).context("Receiver PTP clock became stale")?;
                        (Some(id), ns)
                    } else { (None, clock.now_ns()) };
                    m.media
                        .sync(ns, id, latency, packets == 0);
                }
                next_sync = now + Duration::from_millis(100);
            }
            if let Some(source) = &live {
                let Some((packet, marker)) =
                    source.packet_if_open(f32::from_bits(gain.load(Ordering::Relaxed)))? else { continue; };
                pcm = packet;
                if let Some(qpc) = marker {
                    if qpc.saturating_sub(last_marker) > 200_000_000 {
                        emit(
                            json!({"event":"audio_onset","capture_qpc_ns":qpc,"send_qpc_ns":crate::wasapi::qpc_ns()}),
                        );
                    }
                    last_marker = qpc;
                }
            } else {
                for (j, bytes) in pcm.chunks_exact_mut(2).enumerate() {
                    let index = schedule.frames as usize * 2 + j;
                    let sample = samples.get(index).copied().unwrap_or(0);
                    bytes.copy_from_slice(&sample.to_be_bytes());
                }
            }
            if now >= next_metrics {
                if let Some(source) = &live {
                    emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
                }
                emit(transport_metrics(&members, &schedule));
                next_metrics = now + Duration::from_secs(1);
            }
            // Every member gets new media before any member services retransmissions.
            for member in &mut members {
                member.media.send(
                    &pcm,
                    packets == 0,
                    Instant::now(),
                    schedule.deadline(),
                    &member.health,
                )?;
            }
            if !first_send_reported && members.iter().any(|m| m.media.metrics.packets_sent > 0) {
                emit(json!({"event":"first_send","first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns()}));
                first_send_reported = true;
            }
            for member in &mut members {
                member.media.service_retransmits();
            }
            schedule.sent();
            packets += 1;
        }
        Ok(())
    })();
    if let Some(source) = &live {
        emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
    }
    for member in &members {
        for notice in member.health.notices() {
            emit(notice);
        }
    }
    emit(transport_metrics(&members, &schedule));
    emit(
        json!({"event":"metrics","packets_per_member":packets,"frames":schedule.frames,"max_send_lateness_us":schedule.max_lateness_us,"retransmits":members.iter().map(|m|m.media.metrics.retransmits_sent).sum::<u64>(),"ptp_packets_received":ptp.as_ref().map(|p|p.received.load(Ordering::Relaxed)),"measured_latency_ms":null,"qualified":false}),
    );
    drop(volume_worker);
    for m in &mut members {
        m.close();
    }
    #[cfg(windows)]
    if let Some(microphone) = microphone {
        let recording = microphone.finish()?;
        emit(
            json!({"event":"microphone_recorded","path":recording.path,"sample_rate":recording.sample_rate,"frames":recording.frames}),
        );
    }
    outcome?;
    emit(json!({"event":"stopped","cancelled":cancel.is_cancelled()}));
    Ok(())
}

fn transport_metrics(members: &[Member], schedule: &MediaSchedule) -> Json {
    json!({"event":"transport_metrics","session_uptime_ms":schedule.start.elapsed().as_millis(),"sender_late_recoveries":schedule.recoveries,"skipped_packets":schedule.skipped_packets,"max_send_lateness_us":schedule.max_lateness_us,"members":members.iter().map(Member::metrics).collect::<Vec<_>>()})
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ntp_without_requested_latency_uses_receiver_defaults() {
        for explicit_null in [false, true] {
            let mut params = json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"gain":1.0,"timing":"ntp"});
            if explicit_null { params["latency_ms"] = Json::Null; }
            let parsed = serde_json::from_value::<ProbeOptions>(params);
            assert!(parsed.is_ok(), "NTP must allow omitted or null latency: {parsed:?}");
            let mut options = parsed.unwrap();
            assert!(options.validate().is_ok());
            options.timing = "ptp".into();
            assert!(options.validate().is_err(), "PTP still needs its explicit sync offset");
        }
    }
    #[test]
    fn serialized_setup_omits_defaults_but_preserves_explicit_zero_and_targets() {
        for rate in [44100u32, 48000] {
            for requested in [None, Some(0u32), Some(50), Some(100)] {
                let options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"gain":1.0,"timing":"ntp","sample_rate":rate,"latency_ms":requested})).unwrap();
                options.validate().unwrap();
                let stream = audio_stream(&options, 1234, &[7; 32], 42, true);
                let mut encoded = Vec::new();
                stream.to_writer_binary(&mut encoded).unwrap();
                let decoded = Value::from_reader(std::io::Cursor::new(encoded)).unwrap();
                let fields = decoded.as_dictionary().unwrap();
                assert_eq!(fields["type"].as_unsigned_integer(), Some(96));
                assert_eq!(fields["spf"].as_unsigned_integer(), Some(FRAMES as u64));
                assert_eq!(fields["sr"].as_unsigned_integer(), Some(u64::from(rate)));
                for key in ["latencyMin", "latencyMax"] {
                    if let Some(ms) = requested {
                        assert_eq!(fields[key].as_unsigned_integer(), Some(u64::from(ms) * u64::from(rate) / 1000));
                    } else {
                        assert!(!fields.contains_key(key), "default must omit {key}, not send zero");
                    }
                }
            }
        }
    }
    #[test]
    fn explicit_latency_window_has_independent_bounds() {
        let base = json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"gain":1.0,"timing":"ntp","latency_ms":50,"latency_max_ms":100});
        let parsed = serde_json::from_value::<ProbeOptions>(base.clone());
        assert!(parsed.is_ok(), "explicit range must be accepted: {parsed:?}");
        let options = parsed.unwrap();
        options.validate().unwrap();
        let stream = audio_stream(&options, 1234, &[7; 32], 42, true);
        let fields = stream.as_dictionary().unwrap();
        assert_eq!(fields["latencyMin"].as_unsigned_integer(), Some(2205));
        assert_eq!(fields["latencyMax"].as_unsigned_integer(), Some(4410));
        for (minimum, maximum) in [(Json::Null, json!(100)), (json!(100), json!(50)), (json!(50), json!(2001))] {
            let mut invalid = base.clone(); invalid["latency_ms"] = minimum; invalid["latency_max_ms"] = maximum;
            let invalid: ProbeOptions = serde_json::from_value(invalid).unwrap();
            assert!(invalid.validate().is_err());
        }
    }
    #[test]
    fn range_fallback_does_not_expire_packets_at_its_lower_bound() {
        let mut options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"gain":1.0,"timing":"ntp","latency_ms":50,"latency_max_ms":100})).unwrap();
        assert_eq!(fallback_latency(&options), None, "a requested lower bound is not the receiver's actual playout deadline");
        options.latency_max_ms = Some(50);
        assert_eq!(fallback_latency(&options), Some(Duration::from_millis(50)));
        options.latency_max_ms = None;
        options.latency_ms = Some(0);
        assert_eq!(fallback_latency(&options), Some(Duration::ZERO));
        options.latency_ms = None;
        assert_eq!(fallback_latency(&options), None);
    }
    #[test]
    fn finite_wav_full_gain_preserves_quiet_samples() {
        for rate in [44100, 48000] {
            let mut options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"latency_ms":200,"gain":1.0,"sample_rate":rate})).unwrap();
            options.wav_path = std::env::temp_dir().join(format!("airflash-full-gain-{}.wav", Uuid::new_v4()));
            let mut writer = hound::WavWriter::create(&options.wav_path, hound::WavSpec { channels:2, sample_rate:rate, bits_per_sample:16, sample_format:hound::SampleFormat::Int }).unwrap();
            for sample in [1311i16, -1311, 1638, -1638, 1639, -1639] {
                writer.write_sample(sample).unwrap();
            }
            writer.finalize().unwrap();
            let validation = options.validate();
            let samples = load_quiet_wav(&options);
            std::fs::remove_file(&options.wav_path).unwrap();
            validation.unwrap();
            assert_eq!(samples.unwrap(), [1311, -1311, 1638, -1638, 1639, -1639]);
        }
    }
    #[test]
    fn finite_wav_rejects_peak_above_existing_quiet_cap() {
        let mut options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":5000,"latency_ms":200,"gain":1.0})).unwrap();
        for sample in [1640i16, -1640, i16::MIN, i16::MAX] {
            options.wav_path = std::env::temp_dir().join(format!("airflash-quiet-cap-{}.wav", Uuid::new_v4()));
            let mut writer = hound::WavWriter::create(&options.wav_path, hound::WavSpec { channels:2, sample_rate:44100, bits_per_sample:16, sample_format:hound::SampleFormat::Int }).unwrap();
            writer.write_sample(sample).unwrap(); writer.write_sample(0i16).unwrap(); writer.finalize().unwrap();
            let result = load_quiet_wav(&options);
            std::fs::remove_file(&options.wav_path).unwrap();
            assert!(result.unwrap_err().to_string().contains("peak amplitude <= 0.05"));
        }
    }
    #[test]
    fn unsafe_probe_rejected() {
        let mut o = ProbeOptions {
            peers: vec![Peer {
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                codecs: Vec::new(),
                follow_receiver_clock: false,
            }],
            wav_path: PathBuf::new(),
            source: "wav".into(),
            capture_endpoint: None,
            capture_id: None,
            capture_epoch: None,
            await_volume_gate: None,
            duration_ms: 5001,
            latency_ms: Some(200),
            latency_max_ms: None,
            gain: 0.1,
            equalizer: crate::equalizer::Settings::default(),
            sample_rate: 44100,
            timing: "ptp".into(),
            group_id: None,
            handshake_only: false,
            record_mic_path: None,
        };
        assert!(o.validate().is_err());
        o.duration_ms = 5000;
        for gain in [-0.01, 1.01, f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            o.gain = gain;
            assert!(o.validate().is_err());
        }
        o.gain = 1.0;
        assert!(o.validate().is_ok());
        o.source = "loopback".into();
        assert!(o.validate().is_err());
        o.gain = 0.1;
        assert!(o.validate().is_ok());
        o.source = "wav".into();
        o.equalizer.enabled = true;
        assert!(o.validate().is_err());
        o.source = "loopback".into(); o.duration_ms = 0;
        o.gain = 1.0;
        assert!(o.validate_start().is_ok());
        o.equalizer.preamp_db = f64::NAN;
        assert!(o.validate_start().is_err());
        o.equalizer = crate::equalizer::Settings::default();
        o.source = "wav".into(); o.duration_ms = 5000;
        o.latency_ms = Some(0);
        assert!(o.validate().is_ok());
        o.sample_rate = 48000;
        assert!(o.validate().is_ok());
        o.sample_rate = 96000;
        assert!(o.validate().is_err());
    }
}
