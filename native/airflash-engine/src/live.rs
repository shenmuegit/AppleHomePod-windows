//! Native playback loopback -> one resampler -> bounded PCM queue.
use crate::{
    rtp::FRAMES,
    wasapi::{Mmcss, qpc_ns},
};
use anyhow::{Context, Result, ensure};
use rubato::{
    Resampler, SincFixedIn, SincInterpolationParameters, SincInterpolationType, WindowFunction,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Devices::FunctionDiscovery::PKEY_Device_FriendlyName,
        Foundation::{CloseHandle, WAIT_OBJECT_0},
        Media::Audio::*,
        System::{
            Com::{StructuredStorage::*, *},
            Threading::*,
        },
    },
    core::Interface,
};
const TARGET_MS: usize = 20;
const IDLE_MS: usize = 100;
#[derive(Clone, Copy)]
pub struct Frame {
    pub left: f32,
    pub right: f32,
    pub qpc: u64,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ArmCapture {
    pub capture_id: String,
    pub endpoint_id: String,
    pub sample_rate: u32,
}
impl ArmCapture {
    pub fn validate(&self, armed: bool, running: bool) -> Result<()> {
        ensure!(!armed && !running, "capture or transport already owned; stop and disarm first");
        uuid::Uuid::parse_str(&self.capture_id).context("capture_id must be a UUID")?;
        ensure!(!self.endpoint_id.is_empty() && self.endpoint_id.len() <= 1024 && !self.endpoint_id.contains('\0'), "explicit capture endpoint ID required");
        ensure!(self.sample_rate == 44100, "armed capture requires 44100 Hz");
        Ok(())
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Phase { Legacy, Idle, Buffering { open: bool } }

/// The production queue is also usable without WASAPI for deterministic lifecycle tests.
pub struct CaptureQueue {
    frames: VecDeque<Frame>,
    rate: u32,
    phase: Phase,
    epoch: u64,
    min_qpc: u64,
    overflow: bool,
    dropped: u64,
    has_opened: bool,
}
impl CaptureQueue {
    pub fn legacy(rate: u32) -> Self {
        Self { frames: VecDeque::new(), rate, phase: Phase::Legacy, epoch: 1, min_qpc: 0, overflow: false, dropped: 0, has_opened: false }
    }
    pub fn armed(rate: u32) -> Self { Self { phase: Phase::Idle, ..Self::legacy(rate) } }
    pub fn epoch(&self) -> u64 { self.epoch }
    pub fn len(&self) -> usize { self.frames.len() }
    pub fn is_empty(&self) -> bool { self.frames.is_empty() }
    pub fn dropped_frames(&self) -> u64 { self.dropped }
    pub fn check_epoch(&self, epoch: u64) -> Result<()> {
        ensure!(self.phase != Phase::Legacy && epoch == self.epoch, "stale or invalid capture epoch");
        Ok(())
    }
    pub fn check(&self) -> Result<()> {
        if self.overflow {
            return Err(crate::transport::Fault::new("", "capture", "capture_preroll_overflow", false, "Capture exceeded its 2000 ms bound; reset the epoch before retrying").into());
        }
        Ok(())
    }
    pub fn begin(&mut self, epoch: u64) -> Result<()> {
        self.check_epoch(epoch)?;
        self.check()?;
        if self.phase == Phase::Idle { self.phase = Phase::Buffering { open: false }; }
        Ok(())
    }
    pub fn reset(&mut self, epoch: u64, min_qpc: u64) -> Result<()> {
        self.check_epoch(epoch)?;
        let next = self.epoch.checked_add(1).context("capture epoch exhausted")?;
        self.frames.clear();
        self.overflow = false;
        self.has_opened = false;
        self.epoch = next;
        self.min_qpc = min_qpc;
        self.phase = Phase::Idle;
        Ok(())
    }
    pub fn set_gate(&mut self, epoch: u64, open: bool, clear_pending: bool) -> Result<()> {
        self.set_gate_at(epoch, open, clear_pending, qpc_ns())
    }
    fn set_gate_at(&mut self, epoch: u64, open: bool, clear_pending: bool, now: u64) -> Result<()> {
        self.check_epoch(epoch)?;
        self.check()?;
        ensure!(matches!(self.phase, Phase::Buffering { .. }), "capture has not begun");
        if clear_pending { self.frames.clear(); }
        else if open && !self.has_opened {
            let target = self.rate as usize * TARGET_MS / 1000;
            let recent = self.frames.back().is_some_and(|f| now.abs_diff(f.qpc) <= IDLE_MS as u64 * 1_000_000);
            if recent && self.frames.len() > target && self.frames.iter().rev().take(target).any(|f| f.left != 0.0 || f.right != 0.0) {
                // Live playback must not replay RTSP/volume setup time.
                // shortcut: silence or stale timestamps preserve ended sounds; explicit source timing is needed to distinguish pauses.
                let excess = self.frames.len() - target;
                drop(self.frames.drain(..excess));
                self.dropped += excess as u64;
                self.min_qpc = self.min_qpc.max(self.frames.front().unwrap().qpc);
            }
        }
        self.has_opened |= open;
        self.phase = Phase::Buffering { open };
        Ok(())
    }
    pub fn gate_open(&self) -> Result<bool> {
        self.check()?;
        Ok(matches!(self.phase, Phase::Legacy | Phase::Buffering { open: true }))
    }
    pub fn push(&mut self, frame: Frame) {
        if self.overflow || frame.qpc < self.min_qpc { return; }
        let capacity = match self.phase { Phase::Legacy => self.rate as usize * 60 / 1000, Phase::Idle => self.rate as usize * IDLE_MS / 1000, Phase::Buffering { .. } => self.rate as usize * 2 };
        if self.frames.len() >= capacity {
            if matches!(self.phase, Phase::Buffering { .. }) { self.overflow = true; return; }
            self.frames.pop_front();
            self.dropped += 1;
        }
        self.frames.push_back(frame);
    }
    pub fn pop(&mut self) -> Result<Option<Frame>> {
        ensure!(self.gate_open()?, "capture_gate_closed");
        Ok(self.frames.pop_front())
    }
    fn trim_leading_silence(&mut self, target: usize) {
        if self.overflow || !matches!(self.phase, Phase::Buffering { open: true }) { return; }
        while self.frames.len() > target && self.frames.front().is_some_and(|f| f.left == 0.0 && f.right == 0.0) {
            self.frames.pop_front();
            self.dropped += 1;
        }
    }
    fn resample_correction(&self, target: usize) -> f64 {
        let limit = match self.phase {
            Phase::Legacy => 0.0005,
            Phase::Buffering { open: true } => 0.005,
            _ => return 0.0,
        };
        ((target as f64 - self.len() as f64) / self.rate as f64 * 0.01).clamp(-limit, limit)
    }
    fn discard_stale(&mut self, cutoff: u64, target: usize) {
        if self.phase != Phase::Legacy { self.trim_leading_silence(target); return; }
        while self.frames.front().is_some_and(|f| f.qpc < cutoff) || self.frames.len() > target {
            self.frames.pop_front();
            self.dropped += 1;
        }
    }
}
#[derive(Default, Clone, Serialize)]
pub struct Metrics {
    pub dropped_frames: u64,
    pub underrun_packets: u64,
    pub capture_frames: u64,
    pub max_queue_age_ms: f64,
    pub capture_to_send_p95_ms: Option<f64>,
    pub last_audio_qpc_ns: u64,
    pub input_rate: u32,
}
struct State {
    equalizer: crate::equalizer::Processor,
    queue: CaptureQueue,
    target: usize,
    error: Option<String>,
    metrics: Metrics,
    ages: VecDeque<f64>,
}
impl State {
    fn push_frame(&mut self, frame: Frame) {
        self.queue.push(frame);
    }
}
pub struct Loopback {
    pub capture_id: Option<String>,
    pub endpoint_id: Option<String>,
    pub session_instance_id: String,
    pub sample_rate: u32,
    equalizer_control: crate::equalizer::Control,
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}
impl Loopback {
    pub fn start(endpoint: Option<String>, rate: u32, equalizer: crate::equalizer::Control) -> Result<Self> {
        Self::create(endpoint, rate, equalizer, None)
    }
    pub fn arm(endpoint: String, rate: u32, equalizer: crate::equalizer::Control, capture_id: String) -> Result<Self> {
        ArmCapture { capture_id: capture_id.clone(), endpoint_id: endpoint.clone(), sample_rate: rate }.validate(false, false)?;
        Self::create(Some(endpoint), rate, equalizer, Some(capture_id))
    }
    fn create(endpoint: Option<String>, rate: u32, equalizer: crate::equalizer::Control, capture_id: Option<String>) -> Result<Self> {
        let target = rate as usize * TARGET_MS / 1000;
        let state = Arc::new(Mutex::new(State {
            equalizer: crate::equalizer::Processor::new(equalizer.initial(), rate),
            queue: if capture_id.is_some() { CaptureQueue::armed(rate) } else { CaptureQueue::legacy(rate) },
            target,
            error: None,
            metrics: Metrics::default(),
            ages: VecDeque::with_capacity(4000),
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let child_stop = stop.clone();
        let child_state = state.clone();
        let child_endpoint = endpoint.clone();
        let session_guid = capture_id.as_ref().map(|id| uuid::Uuid::parse_str(id).map(|u| windows::core::GUID::from_u128(u.as_u128()))).transpose()?;
        let (tx, rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("wasapi-capture".into())
            .spawn(move || {
                if let Err(e) = capture(child_endpoint, rate, session_guid, child_stop, &child_state, &tx) {
                    let message = format!("{e:#}");
                    child_state.lock().unwrap().error = Some(message.clone());
                    let _ = tx.try_send(Err(message));
                }
            })?;
        match rx.recv_timeout(Duration::from_secs(3)) {
            Ok(Ok(session_instance_id)) => Ok(Self {
                capture_id,
                endpoint_id: endpoint,
                session_instance_id,
                sample_rate: rate,
                equalizer_control: equalizer,
                state,
                stop,
                worker: Some(worker),
            }),
            result => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                anyhow::bail!("audio capture initialization: {result:?}");
            }
        }
    }
    pub fn ready(&self) -> Result<bool> {
        let s = self.state.lock().unwrap();
        if let Some(e) = &s.error {
            anyhow::bail!("{e}");
        }
        s.queue.check()?;
        Ok(s.queue.len() >= s.target)
    }
    pub fn epoch(&self) -> u64 { self.state.lock().unwrap().queue.epoch() }
    pub fn begin(&self, epoch: u64) -> Result<()> { self.state.lock().unwrap().queue.begin(epoch) }
    pub fn reset(&self, epoch: u64) -> Result<()> { self.state.lock().unwrap().queue.reset(epoch, qpc_ns()) }
    pub fn set_gate(&self, epoch: u64, open: bool, clear_pending: bool) -> Result<()> {
        let mut s = self.state.lock().unwrap();
        s.queue.set_gate(epoch, open, clear_pending)?;
        if clear_pending { s.queue.min_qpc = qpc_ns(); }
        Ok(())
    }
    pub fn gate_open(&self, epoch: u64) -> Result<bool> {
        let s = self.state.lock().unwrap();
        if let Some(e) = &s.error { anyhow::bail!("capture stopped: {e}"); }
        s.queue.check_epoch(epoch)?;
        s.queue.gate_open()
    }
    pub fn validate_key(&self, capture_id: &str, epoch: u64) -> Result<()> {
        ensure!(self.capture_id.as_deref() == Some(capture_id), "capture_id does not match armed capture");
        self.state.lock().unwrap().queue.check_epoch(epoch)
    }
    pub fn ensure_begun(&self) -> Result<()> {
        ensure!(matches!(self.state.lock().unwrap().queue.phase, Phase::Buffering { .. }), "begin_capture must precede start");
        Ok(())
    }
    pub fn packet(&self, gain: f32) -> Result<(Vec<u8>, Option<u64>)> {
        self.packet_if_open(gain)?.context("capture_gate_closed")
    }
    pub fn packet_if_open(&self, gain: f32) -> Result<Option<(Vec<u8>, Option<u64>)>> {
        let mut s = self.state.lock().unwrap();
        if let Some((sequence, prepared)) = self.equalizer_control.latest() { s.equalizer.update(sequence, prepared); }
        if let Some(e) = &s.error {
            anyhow::bail!("capture stopped: {e}");
        }
        if !s.queue.gate_open()? { return Ok(None); }
        let target = s.target;
        s.queue.trim_leading_silence(target);
        let mut out = Vec::with_capacity(FRAMES * 4);
        let mut marker = None;
        let now = qpc_ns();
        let mut underrun = false;
        for _ in 0..FRAMES {
            let frame = if let Some(f) = s.queue.pop()? {
                f
            } else {
                underrun = true;
                Frame {
                    left: 0.0,
                    right: 0.0,
                    qpc: now,
                }
            };
            if frame.left.abs().max(frame.right.abs()) > 0.004 {
                s.metrics.last_audio_qpc_ns = frame.qpc;
                marker.get_or_insert(frame.qpc);
            }
            let age = now.saturating_sub(frame.qpc) as f64 / 1e6;
            s.metrics.max_queue_age_ms = s.metrics.max_queue_age_ms.max(age);
            if s.ages.len() == 4000 {
                s.ages.pop_front();
            }
            s.ages.push_back(age);
            for sample in s.equalizer.frame([frame.left, frame.right]) {
                let value = (sample * gain).clamp(-1.0, 1.0);
                out.extend(((value * 32767.0).round() as i16).to_be_bytes());
            }
        }
        if underrun {
            s.metrics.underrun_packets += 1;
        }
        Ok(Some((out, marker)))
    }
    /// Reclaim stale legacy PCM or excess armed leading silence after a scheduler stall.
    pub fn discard_stale(&self) {
        let mut s = self.state.lock().unwrap();
        let cutoff = qpc_ns().saturating_sub(20_000_000);
        let target = s.target;
        s.queue.discard_stale(cutoff, target);
    }
    pub fn metrics(&self) -> Metrics {
        let s = self.state.lock().unwrap();
        let mut metrics = s.metrics.clone();
        metrics.dropped_frames = s.queue.dropped_frames();
        let mut ages: Vec<_> = s.ages.iter().copied().collect();
        ages.sort_by(f64::total_cmp);
        metrics.capture_to_send_p95_ms = ages.get(ages.len() * 95 / 100).copied();
        metrics
    }
}
impl Drop for Loopback {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct ComGuard;
impl Drop for ComGuard {
    fn drop(&mut self) {
        unsafe { CoUninitialize() }
    }
}
struct AudioGuard(IAudioClient);
impl Drop for AudioGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = self.0.Stop();
        }
    }
}
struct EventGuard(windows::Win32::Foundation::HANDLE);
impl Drop for EventGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
// IDs take precedence over friendly names, including names that resemble an ID.
fn endpoint_index(name: &str, endpoints: &[(String, Option<String>)]) -> Result<usize> {
    if let Some(index) = endpoints.iter().position(|(id, _)| id == name) {
        return Ok(index);
    }
    let mut matches = endpoints.iter().enumerate()
        .filter(|(_, (_, friendly))| friendly.as_deref() == Some(name));
    let Some((index, _)) = matches.next() else {
        anyhow::bail!("capture endpoint unavailable: {name}");
    };
    ensure!(matches.next().is_none(), "capture endpoint name is ambiguous; select a device ID: {name}");
    Ok(index)
}

fn capture_flags(flow: EDataFlow) -> Result<u32> {
    ensure!(flow == eRender, "Recording and microphone endpoints are not supported. Choose a playback output.");
    Ok(AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST | AUDCLNT_STREAMFLAGS_LOOPBACK)
}

fn find_device(enumerator: &IMMDeviceEnumerator, name: Option<&str>) -> Result<IMMDevice> {
    let Some(name) = name.filter(|s| !s.is_empty()) else {
        return Ok(unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eConsole) }?);
    };
    let devices = unsafe { enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) }?;
    let mut endpoints = Vec::new();
    for i in 0..unsafe { devices.GetCount() }? {
        let device = unsafe { devices.Item(i) }?;
        let id = unsafe { device.GetId() }?;
        let identifier = unsafe { id.to_string() };
        unsafe { CoTaskMemFree(Some(id.0.cast())) };
        endpoints.push((identifier?, None));
    }
    // Avoid reading unrelated friendly-name properties when the request uses an ID.
    if let Some(index) = endpoints.iter().position(|(id, _)| id == name) {
        return Ok(unsafe { devices.Item(index as u32) }?);
    }
    for (i, (_, friendly)) in endpoints.iter_mut().enumerate() {
        let device = unsafe { devices.Item(i as u32) }?;
        let store = unsafe { device.OpenPropertyStore(STGM_READ) }?;
        let mut value = unsafe { store.GetValue(&PKEY_Device_FriendlyName) }?;
        if let Ok(text) = unsafe { PropVariantToStringAlloc(&value) } {
            *friendly = unsafe { text.to_string() }.ok();
            unsafe { CoTaskMemFree(Some(text.0.cast())) };
        }
        unsafe { PropVariantClear(&mut value) }?;
    }
    Ok(unsafe { devices.Item(endpoint_index(name, &endpoints)? as u32) }?)
}

fn capture_resampler(rate: u32, stream_rate: u32) -> Result<SincFixedIn<f32>> {
    Ok(SincFixedIn::<f32>::new(
        stream_rate as f64 / rate as f64,
        1.0 / 0.995 + f64::EPSILON, // Keep the reciprocal lower bound from rounding above 0.995.
        SincInterpolationParameters {
            sinc_len: 64,
            f_cutoff: 0.9,
            interpolation: SincInterpolationType::Cubic,
            oversampling_factor: 128,
            window: WindowFunction::BlackmanHarris2,
        },
        (rate / 100) as usize,
        2,
    )?)
}
fn capture(
    endpoint: Option<String>,
    stream_rate: u32,
    session_guid: Option<windows::core::GUID>,
    stop: Arc<AtomicBool>,
    state: &Arc<Mutex<State>>,
    ready: &mpsc::SyncSender<std::result::Result<String, String>>,
) -> Result<()> {
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }?;
    let _com = ComGuard;
    let _priority = Mmcss::new();
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
    let device = if session_guid.is_some() {
        let wide: Vec<u16> = endpoint.as_deref().context("explicit capture endpoint required")?.encode_utf16().chain(Some(0)).collect();
        unsafe { enumerator.GetDevice(windows::core::PCWSTR(wide.as_ptr())) }?
    } else { find_device(&enumerator, endpoint.as_deref())? };
    let flow = unsafe { device.cast::<IMMEndpoint>()?.GetDataFlow() }?;
    let flags = capture_flags(flow)?;
    let client: IAudioClient = unsafe { device.Activate(CLSCTX_ALL, None) }?;
    let pointer = unsafe { client.GetMixFormat() }?;
    let spec = unsafe { pointer.read_unaligned() };
    let channels = spec.nChannels as usize;
    let bits = spec.wBitsPerSample;
    let rate = spec.nSamplesPerSec;
    let tag = if spec.wFormatTag == 0xfffe {
        unsafe { (pointer as *const WAVEFORMATEXTENSIBLE).read_unaligned() }
            .SubFormat
            .data1 as u16
    } else {
        spec.wFormatTag
    };
    let event = EventGuard(unsafe { CreateEventW(None, false, false, None) }?);
    // Prefer the shared engine period; fall back to documented shared initialization.
    let initialized = if let Ok(client3) = client.cast::<IAudioClient3>() {
        let (mut default, mut fundamental, mut minimum, mut maximum) = (0, 0, 0, 0);
        unsafe {
            client3.GetSharedModeEnginePeriod(
                pointer,
                &mut default,
                &mut fundamental,
                &mut minimum,
                &mut maximum,
            )
        }
        .and_then(|_| unsafe { client3.InitializeSharedAudioStream(flags, default, pointer, session_guid.as_ref().map(|g| g as *const _)) })
    } else {
        Err(windows::core::Error::from_hresult(windows::core::HRESULT(
            -1,
        )))
    };
    let initialized = initialized.or_else(|_| unsafe {
        client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200_000, 0, pointer, session_guid.as_ref().map(|g| g as *const _))
    });
    unsafe { CoTaskMemFree(Some(pointer.cast())) };
    initialized?;
    ensure!(
        channels > 0 && channels <= 8 && (8000..=192000).contains(&rate),
        "unsupported capture format"
    );
    ensure!(
        (tag == 3 && bits == 32) || (tag == 1 && [16, 24, 32].contains(&bits)),
        "unsupported capture sample type"
    );
    unsafe { client.SetEventHandle(event.0) }?;
    let reader: IAudioCaptureClient = unsafe { client.GetService() }?;
    let session: IAudioSessionControl = unsafe { client.GetService() }?;
    let session: IAudioSessionControl2 = session.cast()?;
    let instance = unsafe { session.GetSessionInstanceIdentifier() }?;
    let session_instance_id = unsafe { instance.to_string() };
    unsafe { CoTaskMemFree(Some(instance.0.cast())) };
    let session_instance_id = session_instance_id?;
    let chunk = (rate / 100) as usize;
    let mut resampler = capture_resampler(rate, stream_rate)?;
    let mut inputs = [VecDeque::<f32>::new(), VecDeque::<f32>::new()];
    let mut input_times = VecDeque::new();
    let mut capture_epoch = state.lock().unwrap().queue.epoch();
    unsafe { client.Start() }?;
    let _audio = AudioGuard(client);
    state.lock().unwrap().metrics.input_rate = rate;
    let _ = ready.send(Ok(session_instance_id));
    let mut last_packet = Instant::now();
    while !stop.load(Ordering::Acquire) {
        if unsafe { WaitForSingleObject(event.0, 50) } != WAIT_OBJECT_0 {
            // Silent render endpoints may emit no loopback packets. Sender pads silence.
            if last_packet.elapsed() > Duration::from_secs(2) {
                unsafe { reader.GetNextPacketSize() }.context("capture endpoint disconnected")?;
            }
            continue;
        }
        while unsafe { reader.GetNextPacketSize() }? > 0 {
            let epoch = state.lock().unwrap().queue.epoch();
            if epoch != capture_epoch {
                inputs.iter_mut().for_each(VecDeque::clear);
                input_times.clear();
                resampler.reset();
                capture_epoch = epoch;
            }
            let (mut data, mut frames, mut flags, mut position, mut qpc) =
                (std::ptr::null_mut(), 0, 0, 0, 0);
            unsafe {
                reader.GetBuffer(
                    &mut data,
                    &mut frames,
                    &mut flags,
                    Some(&mut position),
                    Some(&mut qpc),
                )
            }?;
            let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
            let timestamp = if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
                qpc_ns().saturating_sub(frames as u64 * 1_000_000_000 / rate as u64)
            } else {
                qpc * 100
            };
            if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
                inputs[0].clear();
                inputs[1].clear();
                input_times.clear();
                resampler.reset();
            }
            let bytes = if data.is_null() {
                &[][..]
            } else {
                unsafe {
                    std::slice::from_raw_parts(data, frames as usize * spec.nBlockAlign as usize)
                }
            };
            for i in 0..frames as usize {
                for (channel, input) in inputs.iter_mut().enumerate() {
                    let value = if silent || bytes.is_empty() {
                        0.0
                    } else {
                        let offset = i * spec.nBlockAlign as usize
                            + channel.min(channels - 1) * bits as usize / 8;
                        let b = &bytes[offset..offset + bits as usize / 8];
                        match (tag, bits) {
                            (3, 32) => f32::from_le_bytes(b.try_into().unwrap()),
                            (1, 16) => i16::from_le_bytes(b.try_into().unwrap()) as f32 / 32768.0,
                            (1, 24) => {
                                i32::from_le_bytes([0, b[0], b[1], b[2]]) as f32 / 2147483648.0
                            }
                            (1, 32) => {
                                i32::from_le_bytes(b.try_into().unwrap()) as f32 / 2147483648.0
                            }
                            _ => unreachable!(),
                        }
                    };
                    input.push_back(if value.is_finite() { value } else { 0.0 });
                }
                input_times.push_back(timestamp + i as u64 * 1_000_000_000 / rate as u64);
            }
            unsafe { reader.ReleaseBuffer(frames) }?;
            last_packet = Instant::now();
            state.lock().unwrap().metrics.capture_frames += frames as u64;
            while inputs[0].len() >= chunk {
                let start = input_times[0];
                let input: Vec<Vec<f32>> = inputs
                    .iter_mut()
                    .map(|q| q.drain(..chunk).collect())
                    .collect();
                input_times.drain(..chunk);
                let correction = {
                    let s = state.lock().unwrap();
                    s.queue.resample_correction(s.target)
                };
                resampler.set_resample_ratio_relative(1.0 + correction, true)?;
                let output = resampler.process(&input, None)?;
                let delay = resampler.output_delay() as u64 * 1_000_000_000 / stream_rate as u64;
                let mut shared = state.lock().unwrap();
                for (i, (&left, &right)) in output[0].iter().zip(&output[1]).enumerate() {
                    shared.push_frame(Frame {
                        left,
                        right,
                        qpc: start.saturating_sub(delay) + i as u64 * 1_000_000_000 / stream_rate as u64,
                    });
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_direction_selects_loopback_only_for_playback() {
        let common = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;
        assert_eq!(capture_flags(eRender).unwrap(), common | AUDCLNT_STREAMFLAGS_LOOPBACK);
        assert!(capture_flags(eCapture).is_err());
        assert!(capture_flags(eAll).is_err());
    }

    #[test]
    fn endpoint_selection_prefers_id_and_rejects_ambiguous_names() {
        let endpoints = vec![
            ("playback-id".into(), Some("recording-id".into())),
            ("recording-id".into(), Some("Voicemeeter Output".into())),
            ("another-id".into(), Some("Voicemeeter Output".into())),
        ];
        assert_eq!(endpoint_index("recording-id", &endpoints).unwrap(), 1);
        assert!(endpoint_index("Voicemeeter Output", &endpoints).unwrap_err().to_string().contains("ambiguous"));
        assert!(endpoint_index("missing", &endpoints).unwrap_err().to_string().contains("unavailable"));
        assert_eq!(endpoint_index("Voicemeeter Output", &endpoints[..2]).unwrap(), 1);
    }

    // Opt-in local WASAPI check: no receiver, playback, or audio file is involved.
    #[test]
    #[ignore = "requires AIRFLASH_CAPTURE_ENDPOINT and a playback endpoint with active audio"]
    fn playback_endpoint_captures_nonzero_pcm() {
        let endpoint = std::env::var("AIRFLASH_CAPTURE_ENDPOINT").expect("playback device ID");
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }.unwrap();
        let _com = ComGuard;
        let enumerator: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }.unwrap();
        let device = find_device(&enumerator, Some(&endpoint)).unwrap();
        let flow = unsafe { device.cast::<IMMEndpoint>().unwrap().GetDataFlow() }.unwrap();
        assert!(flow == eRender, "local check requires a playback endpoint");
        let equalizer = crate::equalizer::Control::new(
            crate::equalizer::Settings::default(), 44100).unwrap();
        let source = Loopback::start(Some(endpoint), 44100, equalizer).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let mut nonzero = false;
        while Instant::now() < deadline {
            thread::sleep(Duration::from_millis(8));
            nonzero |= source.packet(0.1).unwrap().0.iter().any(|b| *b != 0);
        }
        assert!(source.metrics().capture_frames > 0, "no WASAPI frames received");
        assert!(nonzero, "playback endpoint supplied only silence");
        println!("captured {} frames at {} Hz; nonzero PCM verified",
            source.metrics().capture_frames, source.metrics().input_rate);
    }
    const CAPACITY: usize = crate::rtp::RATE as usize * 60 / 1000;
    const TARGET: usize = crate::rtp::RATE as usize * 20 / 1000;
    fn source() -> Loopback {
        let equalizer_control = crate::equalizer::Control::new(crate::equalizer::Settings::default(), crate::rtp::RATE).unwrap();
        Loopback {
            capture_id: None,
            endpoint_id: None,
            session_instance_id: "test".into(),
            sample_rate: crate::rtp::RATE,
            state: Arc::new(Mutex::new(State {
                equalizer: crate::equalizer::Processor::new(equalizer_control.initial(), crate::rtp::RATE),
                queue: CaptureQueue::legacy(crate::rtp::RATE),
                target: TARGET,
                error: None,
                metrics: Metrics::default(),
                ages: VecDeque::new(),
            })),
            stop: Arc::new(AtomicBool::new(false)),
            worker: None,
            equalizer_control,
        }
    }
    fn armed_source() -> Loopback {
        let mut source = source();
        source.capture_id = Some("266c0a08-eec2-4564-944b-53ca1a54e97f".into());
        source.state.lock().unwrap().queue = CaptureQueue::armed(crate::rtp::RATE);
        source.begin(1).unwrap();
        source
    }
    #[test]
    fn first_open_catches_up_continuous_music_without_replaying_handshake_backlog() {
        let source = armed_source();
        let count = crate::rtp::RATE as usize * 9 / 10;
        {
            let mut s = source.state.lock().unwrap();
            for i in 0..count { s.push_frame(Frame { left: 0.02, right: -0.02, qpc: i as u64 + 1 }); }
        }
        assert!(source.packet_if_open(1.0).unwrap().is_none());
        source.state.lock().unwrap().queue.set_gate_at(1, true, false, count as u64).unwrap();
        {
            let mut s = source.state.lock().unwrap();
            assert_eq!(s.queue.len(), TARGET, "900ms of live preroll must not become permanent playback delay");
            assert_eq!(s.queue.dropped_frames(), (count - TARGET) as u64);
            s.push_frame(Frame { left: 0.03, right: 0.0, qpc: (count - TARGET) as u64 });
            assert_eq!(s.queue.len(), TARGET, "an older in-flight resampler batch must stay rejected");
        }
        let (pcm, marker) = source.packet(1.0).unwrap();
        assert_eq!(&pcm[..4], &[2, 143, 253, 113]);
        assert_eq!(marker, Some((count - TARGET + 1) as u64));
    }
    #[test]
    fn first_open_catchup_occurs_once_per_epoch_and_never_on_later_unmute() {
        let mut queue = CaptureQueue::armed(crate::rtp::RATE);
        for epoch in 1..=2 {
            queue.begin(epoch).unwrap();
            for i in 0..8820 { queue.push(Frame { left: 0.02, right: 0.0, qpc: i + 1 }); }
            assert!(queue.set_gate(epoch + 1, true, false).is_err());
            queue.set_gate_at(epoch, true, false, 10000).unwrap();
            assert_eq!(queue.len(), TARGET);
            let dropped = queue.dropped_frames();
            queue.set_gate(epoch, false, false).unwrap();
            for i in 0..8820 { queue.push(Frame { left: 0.03, right: 0.0, qpc: i + 10000 }); }
            queue.set_gate(epoch, true, false).unwrap();
            assert_eq!(queue.len(), TARGET + 8820);
            assert_eq!(queue.dropped_frames(), dropped);
            queue.reset(epoch, 0).unwrap();
        }
    }
    #[test]
    fn first_open_catchup_does_not_treat_quiet_or_right_channel_music_as_silence() {
        for (left, right) in [(0.02, 0.0), (0.0, 0.02), (f32::MIN_POSITIVE, 0.0)] {
            let mut queue = CaptureQueue::armed(crate::rtp::RATE);
            queue.begin(1).unwrap();
            for i in 0..TARGET * 2 { queue.push(Frame { left, right, qpc: i as u64 + 1 }); }
            queue.set_gate_at(1, true, false, (TARGET * 2) as u64).unwrap();
            assert_eq!(queue.len(), TARGET);
            assert_eq!(queue.dropped_frames(), TARGET as u64);
        }
    }
    #[test]
    fn first_open_preserves_ended_sound_when_silent_endpoint_stops_delivering_packets() {
        let mut queue = CaptureQueue::armed(crate::rtp::RATE);
        queue.begin(1).unwrap();
        let stopped = qpc_ns().saturating_sub(800_000_000);
        let start = stopped.saturating_sub(100_000_000);
        for i in 0..4410 {
            queue.push(Frame { left: 0.02, right: 0.0, qpc: start + i * 1_000_000_000 / crate::rtp::RATE as u64 });
        }
        queue.set_gate(1, true, false).unwrap();
        assert_eq!(queue.len(), 4410, "no new packets must not erase an ended startup notification");
        assert_eq!(queue.dropped_frames(), 0);
        assert_eq!(queue.pop().unwrap().unwrap().qpc, start);
    }
    #[test]
    fn armed_packet_keeps_closed_preroll_then_trims_open_silence_to_target() {
        let source = armed_source();
        let count = crate::rtp::RATE as usize;
        {
            let mut s = source.state.lock().unwrap();
            for i in 0..count { s.push_frame(Frame { left: 0.0, right: -0.0, qpc: i as u64 + 1 }); }
        }
        assert!(source.packet_if_open(1.0).unwrap().is_none());
        {
            let s = source.state.lock().unwrap();
            assert_eq!(s.queue.len(), count);
            assert_eq!(s.queue.dropped_frames(), 0);
            assert_eq!(s.queue.frames.front().unwrap().qpc, 1);
        }
        source.set_gate(1, true, false).unwrap();
        let (pcm, marker) = source.packet(1.0).unwrap();
        assert!(pcm.iter().all(|b| *b == 0));
        assert!(marker.is_none());
        let s = source.state.lock().unwrap();
        assert_eq!(s.queue.len(), TARGET - FRAMES);
        assert_eq!(s.queue.dropped_frames(), (count - TARGET) as u64);
        assert_eq!(s.queue.frames.front().unwrap().qpc, (count - TARGET + FRAMES + 1) as u64);
    }
    #[test]
    fn armed_silence_trim_preserves_even_tiny_nonzero_and_the_other_channel() {
        let source = armed_source();
        let count = crate::rtp::RATE as usize;
        {
            let mut s = source.state.lock().unwrap();
            for i in 0..count { s.push_frame(Frame { left: 0.0, right: 0.0, qpc: i as u64 + 1 }); }
            s.push_frame(Frame { left: f32::MIN_POSITIVE, right: 0.0, qpc: count as u64 + 1 });
            s.push_frame(Frame { left: 0.0, right: 0.02, qpc: count as u64 + 2 });
            for i in 0..TARGET + FRAMES { s.push_frame(Frame { left: 0.0, right: 0.0, qpc: (count + i + 3) as u64 }); }
        }
        source.set_gate(1, true, false).unwrap();
        let (pcm, marker) = source.packet(1.0).unwrap();
        assert_eq!(&pcm[..8], &[0, 0, 0, 0, 0, 0, 2, 143]);
        assert_eq!(marker, Some(count as u64 + 2));
        assert_eq!(source.metrics().dropped_frames, count as u64);
    }
    #[test]
    fn armed_recovery_and_packet_consumption_preserve_nonzero_audio_after_first_open() {
        let source = armed_source();
        source.set_gate(1, true, false).unwrap();
        source.set_gate(1, false, false).unwrap();
        {
            let mut s = source.state.lock().unwrap();
            for i in 0..8820 { s.push_frame(Frame { left: 0.01, right: -0.01, qpc: i + 1 }); }
        }
        source.discard_stale();
        assert!(source.packet_if_open(1.0).unwrap().is_none());
        assert_eq!(source.state.lock().unwrap().queue.len(), 8820);
        source.set_gate(1, true, false).unwrap();
        source.discard_stale();
        let (pcm, marker) = source.packet(1.0).unwrap();
        assert_eq!(&pcm[..4], &[1, 72, 254, 184]);
        assert_eq!(marker, Some(1));
        assert_eq!(source.state.lock().unwrap().queue.len(), 8820 - FRAMES);
        assert_eq!(source.metrics().dropped_frames, 0);
    }
    #[test]
    fn armed_reset_keeps_the_new_gate_closed_and_rejects_previous_timestamps() {
        let source = armed_source();
        source.state.lock().unwrap().push_frame(Frame { left: 0.02, right: 0.0, qpc: 1 });
        source.set_gate(1, true, false).unwrap();
        source.reset(1).unwrap();
        let fresh = {
            let mut s = source.state.lock().unwrap();
            let cutoff = s.queue.min_qpc;
            s.push_frame(Frame { left: 0.02, right: 0.0, qpc: cutoff - 1 });
            s.push_frame(Frame { left: -0.03, right: 0.0, qpc: cutoff + 1 });
            assert_eq!(s.queue.len(), 1);
            cutoff + 1
        };
        assert_eq!(source.epoch(), 2);
        assert!(source.set_gate(1, true, false).is_err());
        source.begin(2).unwrap();
        assert!(source.packet_if_open(1.0).unwrap().is_none());
        source.set_gate(2, true, false).unwrap();
        let (pcm, marker) = source.packet(1.0).unwrap();
        assert_eq!(&pcm[..4], &[252, 41, 0, 0]);
        assert_eq!(marker, Some(fresh));
    }
    #[test]
    fn armed_feedback_is_neutral_until_open_and_bounds_both_directions() {
        let mut queue = CaptureQueue::armed(crate::rtp::RATE);
        assert_eq!(queue.resample_correction(TARGET), 0.0);
        queue.begin(1).unwrap();
        queue.set_gate(1, true, false).unwrap();
        queue.set_gate(1, false, false).unwrap();
        for i in 0..52920 { queue.push(Frame { left: 0.02, right: -0.02, qpc: i + 1 }); }
        assert_eq!(queue.resample_correction(TARGET), 0.0);
        queue.set_gate(1, true, false).unwrap();
        assert_eq!(queue.resample_correction(TARGET), -0.005);
        queue.set_gate(1, false, false).unwrap();
        assert_eq!(queue.resample_correction(TARGET), 0.0);
        queue.reset(1, 60000).unwrap();
        assert_eq!(queue.resample_correction(TARGET), 0.0);
        queue.begin(2).unwrap();
        assert_eq!(queue.resample_correction(TARGET), 0.0);
        queue.set_gate(2, true, false).unwrap();
        assert!(queue.resample_correction(TARGET) > 0.0);
        assert!(queue.resample_correction(TARGET) <= 0.005);
        let mut legacy = CaptureQueue::legacy(crate::rtp::RATE);
        assert!(legacy.resample_correction(TARGET) > 0.0);
        for _ in 0..CAPACITY { legacy.push(Frame { left: 0.02, right: -0.02, qpc: 1 }); }
        assert!(legacy.resample_correction(TARGET) < 0.0);
        assert_eq!(legacy.resample_correction(0), -0.0005);
    }
    #[test]
    fn armed_resampler_accepts_half_percent_and_changes_real_output_counts() {
        for input_rate in [8000, 44100, 48000, 96000, 192000] {
            let mut resampler = capture_resampler(input_rate, 44100).unwrap();
            let input = vec![vec![0.02; (input_rate / 100) as usize]; 2];
            resampler.set_resample_ratio_relative(0.995, true).unwrap();
            let mut fewer = 0;
            for _ in 0..100 { fewer += resampler.process(&input, None).unwrap()[0].len(); }
            assert!((43600..44100).contains(&fewer), "input_rate={input_rate}, output={fewer}");
            resampler.reset();
            resampler.set_resample_ratio_relative(1.005, true).unwrap();
            let mut more = 0;
            for _ in 0..100 { more += resampler.process(&input, None).unwrap()[0].len(); }
            assert!((44100..44400).contains(&more), "input_rate={input_rate}, output={more}");
            println!("{input_rate}->44100: 1s input produced {fewer} frames at 0.995 and {more} at 1.005");
        }
    }
    #[test]
    fn armed_feedback_reduces_continuous_audio_backlog_without_queue_drops() {
        let rate = crate::rtp::RATE;
        let chunk = (rate / 100) as usize;
        let initial = rate as usize * 12 / 10;
        let mut queue = CaptureQueue::armed(rate);
        queue.begin(1).unwrap();
        queue.set_gate(1, true, false).unwrap();
        for i in 0..initial { queue.push(Frame { left: 0.02, right: -0.02, qpc: i as u64 + 1 }); }
        let mut resampler = capture_resampler(rate, rate).unwrap();
        let input = vec![vec![0.02; chunk], vec![-0.02; chunk]];
        let mut produced = 0;
        let mut after_ten_seconds = 0;
        for batch in 0..2000 {
            let correction = queue.resample_correction(TARGET);
            assert!((-0.005..=0.005).contains(&correction));
            resampler.set_resample_ratio_relative(1.0 + correction, true).unwrap();
            let output = resampler.process(&input, None).unwrap();
            produced += output[0].len();
            for (&left, &right) in output[0].iter().zip(&output[1]) { queue.push(Frame { left, right, qpc: (initial + batch * chunk) as u64 + 1 }); }
            for _ in 0..chunk { queue.pop().unwrap().unwrap(); }
            queue.check().unwrap();
            if batch == 999 { after_ten_seconds = queue.len(); }
        }
        let reduction = initial - queue.len();
        assert!((2000..2300).contains(&(initial - after_ten_seconds)));
        assert!((4000..4500).contains(&reduction), "backlog only shrank by {reduction} frames");
        assert_eq!(queue.len(), initial + produced - 2000 * chunk);
        assert_eq!(queue.dropped_frames(), 0);
        println!("1.2s nonzero backlog: {initial} -> {after_ten_seconds} at 10s -> {} at 20s, {produced} output frames, no queue drops", queue.len());
    }
    #[test]
    fn underrun_pads_silence_and_can_resume_audio() {
        let source = source();
        assert!(source.packet(0.1).unwrap().0.iter().all(|b| *b == 0));
        assert_eq!(source.metrics().underrun_packets, 1);
        for _ in 0..FRAMES {
            source.state.lock().unwrap().push_frame(Frame {
                left: 0.01,
                right: 0.01,
                qpc: qpc_ns(),
            });
        }
        assert!(source.packet(0.1).unwrap().0.iter().any(|b| *b != 0));
        assert_eq!(source.metrics().underrun_packets, 1);
    }
    #[test]
    fn equalizer_precedes_master_gain_and_pcm_conversion() {
        let source = source();
        let settings = crate::equalizer::Settings { enabled: true, preamp_db: -12.0, ..Default::default() };
        source.equalizer_control.set(1, crate::equalizer::Prepared::new(settings, crate::rtp::RATE).unwrap());
        for _ in 0..FRAMES * 3 { source.state.lock().unwrap().push_frame(Frame { left: 0.25, right: -0.25, qpc: qpc_ns() }); }
        source.packet(0.5).unwrap(); source.packet(0.5).unwrap();
        let (pcm, marker) = source.packet(0.5).unwrap();
        let last = &pcm[pcm.len() - 4..];
        let expected = (0.25 * 10f32.powf(-12.0 / 20.0) * 0.5 * 32767.0).round() as i16;
        assert_eq!(i16::from_be_bytes([last[0], last[1]]), expected);
        assert_eq!(i16::from_be_bytes([last[2], last[3]]), -expected);
        assert!(marker.is_some());
    }
    #[test]
    fn overflow_and_scheduler_recovery_drop_old_frames_without_failure() {
        let source = source();
        {
            let mut s = source.state.lock().unwrap();
            for _ in 0..CAPACITY + 100 {
                s.push_frame(Frame {
                    left: 0.01,
                    right: 0.01,
                    qpc: 0,
                });
            }
            assert_eq!(s.queue.len(), CAPACITY);
        }
        assert_eq!(source.metrics().dropped_frames, 100);
        source.discard_stale();
        assert_eq!(source.metrics().dropped_frames, (CAPACITY + 100) as u64);
        assert!(source.packet(0.1).unwrap().0.iter().all(|b| *b == 0));
        assert_eq!(source.metrics().underrun_packets, 1);
    }
}
