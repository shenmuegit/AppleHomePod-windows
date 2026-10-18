//! HAP SRP-6a, authenticated persistent pairing and ephemeral pair verification.
use crate::{
    crypto::{derive, tlv_decode, tlv_encode},
    rtsp::Connection,
};
use anyhow::{Context, Result, ensure};
use num_bigint::BigUint;
use rand::{RngCore, rngs::OsRng};
use sha2::{Digest, Sha512};
use subtle::ConstantTimeEq;
use zeroize::Zeroizing;
const PRIME: &str = concat!(
    "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA6",
    "3B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245",
    "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F2411",
    "7C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F",
    "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08",
    "CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9",
    "DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D",
    "04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7",
    "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D8760273",
    "3EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB31",
    "43DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF"
);
fn hash(parts: &[&[u8]]) -> Vec<u8> {
    let mut h = Sha512::new();
    for part in parts {
        h.update(part);
    }
    h.finalize().to_vec()
}
fn int(bytes: &[u8]) -> BigUint {
    BigUint::from_bytes_be(bytes)
}
fn pad(n: &BigUint) -> Vec<u8> {
    let b = n.to_bytes_be();
    let mut out = vec![0; 384 - b.len()];
    out.extend(b);
    out
}
pub struct Proof {
    pub public: Vec<u8>,
    pub proof: Vec<u8>,
    pub expected: Vec<u8>,
    pub key: Zeroizing<Vec<u8>>,
}
/// HAP uses unpadded g in M1, but padded g in k and padded A/B in u.
pub fn client_proof(private: &[u8], salt: &[u8], server: &[u8]) -> Result<Proof> {
    client_proof_pin(private, salt, server, "3939")
}
fn client_proof_pin(private: &[u8], salt: &[u8], server: &[u8], pin: &str) -> Result<Proof> {
    ensure!(
        !salt.is_empty() && salt.len() <= 64 && !server.is_empty() && server.len() <= 384,
        "invalid SRP parameters"
    );
    let n = BigUint::parse_bytes(PRIME.as_bytes(), 16).unwrap();
    let g = BigUint::from(5u8);
    let b = int(server);
    ensure!(
        &b % &n != BigUint::from(0u8),
        "invalid SRP server public value"
    );
    let a = int(private);
    let public = g.modpow(&a, &n);
    let public_bytes = public.to_bytes_be();
    let k = int(&hash(&[&n.to_bytes_be(), &pad(&g)]));
    let x = int(&hash(&[salt, &hash(&[b"Pair-Setup:", pin.as_bytes()])]));
    let u = int(&hash(&[&pad(&public), &pad(&b)]));
    ensure!(u != BigUint::from(0u8), "invalid scrambling parameter");
    let kgx = (k * g.modpow(&x, &n)) % &n;
    let base = (&b + &n - kgx) % &n;
    let s = base.modpow(&(a + u * x), &n);
    let key = Zeroizing::new(hash(&[&s.to_bytes_be()]));
    let hn = hash(&[&n.to_bytes_be()]);
    let hg = hash(&[&g.to_bytes_be()]);
    let xor: Vec<u8> = hn.iter().zip(&hg).map(|(a, b)| a ^ b).collect();
    let proof = hash(&[
        &xor,
        &hash(&[b"Pair-Setup"]),
        salt,
        &public_bytes,
        server,
        &key,
    ]);
    let expected = hash(&[&public_bytes, &proof, &key]);
    Ok(Proof {
        public: public_bytes,
        proof,
        expected,
        key,
    })
}
fn srp_handshake(conn: &mut Connection, pin: &str, transient: bool) -> Result<Zeroizing<Vec<u8>>> {
    srp_handshake_prompt(conn, transient, || Ok(pin.to_owned()))
}
fn srp_handshake_prompt(
    conn: &mut Connection,
    transient: bool,
    get_pin: impl FnOnce() -> Result<String>,
) -> Result<Zeroizing<Vec<u8>>> {
    let headers = [
        ("X-Apple-HKP", if transient { "4" } else { "3" }.into()),
        ("Content-Type", "application/octet-stream".into()),
    ];
    conn.request("POST", "/pair-pin-start", &headers, &[])?;
    let m1 = if transient {
        tlv_encode(&[(0, &[0]), (6, &[1]), (0x13, &[0x10])])
    } else {
        tlv_encode(&[(0, &[0]), (6, &[1])])
    };
    let m2 = tlv_decode(&conn.request("POST", "/pair-setup", &headers, &m1)?.body)?;
    ensure!(m2.get(&6) == Some(&vec![2]), "expected pairing M2");
    let pin = Zeroizing::new(get_pin()?);
    ensure!(
        (4..=8).contains(&pin.len()) && pin.bytes().all(|b| b.is_ascii_digit()),
        "PIN must contain 4..8 digits"
    );
    let mut private = Zeroizing::new([0u8; 32]);
    OsRng.fill_bytes(private.as_mut());
    let proof = client_proof_pin(
        private.as_ref(),
        m2.get(&2).context("missing salt")?,
        m2.get(&3).context("missing server key")?,
        &pin,
    )?;
    let m3 = tlv_encode(&[(6, &[3]), (3, &proof.public), (4, &proof.proof)]);
    let m4 = tlv_decode(&conn.request("POST", "/pair-setup", &headers, &m3)?.body)?;
    ensure!(m4.get(&6) == Some(&vec![4]), "expected pairing M4");
    ensure!(
        bool::from(
            m4.get(&4)
                .context("missing server proof")?
                .as_slice()
                .ct_eq(&proof.expected)
        ),
        "SRP server proof mismatch"
    );
    Ok(proof.key)
}
fn enable_control(conn: &mut Connection, key: &[u8]) {
    conn.encrypt(
        derive(key, "Control-Salt", "Control-Write-Encryption-Key"),
        derive(key, "Control-Salt", "Control-Read-Encryption-Key"),
    );
}
pub fn transient(conn: &mut Connection) -> Result<Zeroizing<Vec<u8>>> {
    let key = srp_handshake(conn, "3939", true)?;
    enable_control(conn, &key);
    Ok(key)
}

#[derive(serde::Serialize, serde::Deserialize, zeroize::Zeroize, zeroize::ZeroizeOnDrop)]
pub struct Credentials {
    pub accessory_id: Vec<u8>,
    pub accessory_public: [u8; 32],
    pub controller_id: Vec<u8>,
    pub controller_secret: [u8; 32],
}

pub fn pair(conn: &mut Connection, pin: &str) -> Result<Credentials> {
    pair_prompt(conn, || Ok(pin.to_owned()))
}
pub fn pair_prompt(
    conn: &mut Connection,
    get_pin: impl FnOnce() -> Result<String>,
) -> Result<Credentials> {
    use crate::crypto::{auth_open, auth_seal};
    use ed25519_dalek::{Signature, Signer, SigningKey, VerifyingKey};
    let key = srp_handshake_prompt(conn, false, get_pin)?;
    let mut secret = Zeroizing::new([0; 32]);
    OsRng.fill_bytes(secret.as_mut());
    let signing = SigningKey::from_bytes(&secret);
    let id = uuid::Uuid::new_v4().to_string().into_bytes();
    let public = signing.verifying_key().to_bytes();
    let x = derive(
        &key,
        "Pair-Setup-Controller-Sign-Salt",
        "Pair-Setup-Controller-Sign-Info",
    );
    let signature = signing
        .sign(&[x.as_slice(), &id, &public].concat())
        .to_bytes();
    let session = derive(&key, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
    let encrypted = auth_seal(
        &session,
        b"PS-Msg05",
        &tlv_encode(&[(1, &id), (3, &public), (10, &signature)]),
    )?;
    let headers = [
        ("X-Apple-HKP", "3".into()),
        ("Content-Type", "application/octet-stream".into()),
    ];
    let reply = tlv_decode(
        &conn
            .request(
                "POST",
                "/pair-setup",
                &headers,
                &tlv_encode(&[(6, &[5]), (5, &encrypted)]),
            )?
            .body,
    )?;
    ensure!(reply.get(&6) == Some(&vec![6]), "expected pairing M6");
    let peer = tlv_decode(&auth_open(
        &session,
        b"PS-Msg06",
        reply.get(&5).context("missing encrypted M6")?,
    )?)?;
    let accessory_id = peer
        .get(&1)
        .context("missing accessory identifier")?
        .clone();
    let accessory_public: [u8; 32] = peer
        .get(&3)
        .context("missing accessory public key")?
        .as_slice()
        .try_into()?;
    let signature = Signature::from_slice(peer.get(&10).context("missing accessory signature")?)?;
    let x = derive(
        &key,
        "Pair-Setup-Accessory-Sign-Salt",
        "Pair-Setup-Accessory-Sign-Info",
    );
    VerifyingKey::from_bytes(&accessory_public)?
        .verify_strict(
            &[x.as_slice(), &accessory_id, &accessory_public].concat(),
            &signature,
        )
        .context("accessory signature mismatch")?;
    Ok(Credentials {
        accessory_id,
        accessory_public,
        controller_id: id,
        controller_secret: *secret,
    })
}
pub fn verify(conn: &mut Connection, credentials: &Credentials) -> Result<Zeroizing<Vec<u8>>> {
    use crate::crypto::{auth_open, auth_seal};
    use ed25519_dalek::{Signature, Signer, SigningKey, VerifyingKey};
    use x25519_dalek::{PublicKey, StaticSecret};
    let mut random = Zeroizing::new([0; 32]);
    OsRng.fill_bytes(random.as_mut());
    let private = StaticSecret::from(*random);
    let public = PublicKey::from(&private);
    let headers = [
        ("X-Apple-HKP", "3".into()),
        ("Content-Type", "application/octet-stream".into()),
    ];
    let m2 = tlv_decode(
        &conn
            .request(
                "POST",
                "/pair-verify",
                &headers,
                &tlv_encode(&[(6, &[1]), (3, public.as_bytes())]),
            )?
            .body,
    )?;
    ensure!(m2.get(&6) == Some(&vec![2]), "expected verification M2");
    let peer_public: [u8; 32] = m2
        .get(&3)
        .context("missing verification public key")?
        .as_slice()
        .try_into()?;
    let shared = private.diffie_hellman(&PublicKey::from(peer_public));
    ensure!(shared.was_contributory(), "invalid X25519 public key");
    let key = Zeroizing::new(shared.as_bytes().to_vec());
    let session = derive(&key, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
    let peer = tlv_decode(&auth_open(
        &session,
        b"PV-Msg02",
        m2.get(&5).context("missing encrypted verification")?,
    )?)?;
    ensure!(
        peer.get(&1) == Some(&credentials.accessory_id),
        "accessory identity mismatch"
    );
    let signature =
        Signature::from_slice(peer.get(&10).context("missing verification signature")?)?;
    VerifyingKey::from_bytes(&credentials.accessory_public)?
        .verify_strict(
            &[
                peer_public.as_slice(),
                &credentials.accessory_id,
                public.as_bytes(),
            ]
            .concat(),
            &signature,
        )
        .context("accessory verification signature mismatch")?;
    let signed = SigningKey::from_bytes(&credentials.controller_secret)
        .sign(
            &[
                public.as_bytes().as_slice(),
                &credentials.controller_id,
                &peer_public,
            ]
            .concat(),
        )
        .to_bytes();
    let encrypted = auth_seal(
        &session,
        b"PV-Msg03",
        &tlv_encode(&[(1, &credentials.controller_id), (10, &signed)]),
    )?;
    let m4 = tlv_decode(
        &conn
            .request(
                "POST",
                "/pair-verify",
                &headers,
                &tlv_encode(&[(6, &[3]), (5, &encrypted)]),
            )?
            .body,
    )?;
    ensure!(m4.get(&6) == Some(&vec![4]), "expected verification M4");
    enable_control(conn, &key);
    Ok(key)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reject_invalid_server_public() {
        assert!(client_proof(&[7; 32], &[8; 16], &[0]).is_err());
    }
}

#[cfg(test)]
mod protocol_tests {
    use super::*;
    use crate::{
        crypto::{auth_open, auth_seal},
        rtsp::{Cancellation, Message},
    };
    use ed25519_dalek::{Signature, Signer, SigningKey, VerifyingKey};
    use std::{net::TcpListener, thread};
    fn respond(c: &mut Connection, request: &Message, body: &[u8]) {
        let seq = &request.headers["cseq"];
        c.write(
            format!(
                "HTTP/1.1 200 OK\r\nCSeq: {seq}\r\nContent-Length: {}\r\n\r\n",
                body.len()
            )
            .as_bytes(),
        )
        .unwrap();
        c.write(body).unwrap();
    }
    fn server_srp(c: &mut Connection, pin: &str) -> Vec<u8> {
        let request = c.read().unwrap();
        assert!(request.first.contains("/pair-pin-start"));
        respond(c, &request, &[]);
        let request = c.read().unwrap();
        let m1 = tlv_decode(&request.body).unwrap();
        assert_eq!(m1[&6], vec![1]);
        let n = BigUint::parse_bytes(PRIME.as_bytes(), 16).unwrap();
        let g = BigUint::from(5u8);
        let salt = vec![8; 16];
        let b = BigUint::from(1234567u64);
        let k = int(&hash(&[&n.to_bytes_be(), &pad(&g)]));
        let x = int(&hash(&[&salt, &hash(&[b"Pair-Setup:", pin.as_bytes()])]));
        let v = g.modpow(&x, &n);
        let public = (&k * &v + g.modpow(&b, &n)) % &n;
        let public_bytes = public.to_bytes_be();
        respond(
            c,
            &request,
            &tlv_encode(&[(6, &[2]), (2, &salt), (3, &public_bytes)]),
        );
        let request = c.read().unwrap();
        let m3 = tlv_decode(&request.body).unwrap();
        let a = int(&m3[&3]);
        let u = int(&hash(&[&pad(&a), &pad(&public)]));
        let shared = (a.clone() * v.modpow(&u, &n)).modpow(&b, &n);
        let key = hash(&[&shared.to_bytes_be()]);
        let xor: Vec<_> = hash(&[&n.to_bytes_be()])
            .iter()
            .zip(hash(&[&g.to_bytes_be()]))
            .map(|(a, b)| a ^ b)
            .collect();
        let expected = hash(&[
            &xor,
            &hash(&[b"Pair-Setup"]),
            &salt,
            &m3[&3],
            &public_bytes,
            &key,
        ]);
        assert_eq!(m3[&4], expected);
        let proof = hash(&[&m3[&3], &expected, &key]);
        respond(c, &request, &tlv_encode(&[(6, &[4]), (4, &proof)]));
        key
    }
    #[test]
    fn handshake_only_reads_validated_volume_without_record_media_or_volume_writes() {
        use crate::session::{ProbeOptions, probe};
        use serde_json::json;
        use std::{net::UdpSocket, sync::{Arc, Mutex, atomic::AtomicU32}};

        for (reported, expected) in [
            (Some(plist::Value::Real(-21.6)), Some(28)),
            (Some(plist::Value::Real(-144.0)), Some(0)),
            (None, None),
            (Some(plist::Value::String("28".into())), None),
            (Some(plist::Value::Real(1.0)), None),
        ] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let addr = listener.local_addr().unwrap();
            let events_listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let event_port = events_listener.local_addr().unwrap().port();
            let audio = UdpSocket::bind("127.0.0.1:0").unwrap();
            let control = UdpSocket::bind("127.0.0.1:0").unwrap();
            let data_port = audio.local_addr().unwrap().port();
            let control_port = control.local_addr().unwrap().port();
            let server = thread::spawn(move || {
                let (socket, _) = listener.accept().unwrap();
                let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
                let request = c.read().unwrap();
                assert!(request.first.starts_with("GET /info "));
                let mut body = Vec::new();
                plist::Value::Dictionary(plist::Dictionary::new()).to_writer_binary(&mut body).unwrap();
                respond(&mut c, &request, &body);
                let key = server_srp(&mut c, "3939");
                c.encrypt(derive(&key, "Control-Salt", "Control-Read-Encryption-Key"),
                    derive(&key, "Control-Salt", "Control-Write-Encryption-Key"));
                let request = c.read().unwrap();
                assert!(request.first.starts_with("SETUP "));
                let mut body = Vec::new();
                plist::Value::Dictionary([("eventPort".to_owned(), plist::Value::Integer(event_port.into()))].into_iter().collect())
                    .to_writer_binary(&mut body).unwrap();
                respond(&mut c, &request, &body);
                let (_events, _) = events_listener.accept().unwrap();
                let request = c.read().unwrap();
                assert!(request.first.starts_with("SETUP "));
                let stream = plist::Value::Dictionary([
                    ("dataPort".to_owned(), plist::Value::Integer(data_port.into())),
                    ("controlPort".to_owned(), plist::Value::Integer(control_port.into())),
                ].into_iter().collect());
                let mut body = Vec::new();
                plist::Value::Dictionary([("streams".to_owned(), plist::Value::Array(vec![stream]))].into_iter().collect())
                    .to_writer_binary(&mut body).unwrap();
                respond(&mut c, &request, &body);
                let mut volume_reads = 0;
                loop {
                    let request = c.read().unwrap();
                    if request.first.starts_with("TEARDOWN ") {
                        respond(&mut c, &request, &[]);
                        break;
                    }
                    assert!(request.first.starts_with("GET /info "), "handshake-only must not send RECORD, FLUSH or SET_PARAMETER: {}", request.first);
                    volume_reads += 1;
                    let mut info = plist::Dictionary::new();
                    if let Some(value) = reported.clone() { info.insert("initialVolume".into(), value); }
                    let mut body = Vec::new();
                    plist::Value::Dictionary(info).to_writer_binary(&mut body).unwrap();
                    respond(&mut c, &request, &body);
                }
                volume_reads
            });
            let emitted = Mutex::new(Vec::new());
            let options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1","port":addr.port()}],
                "source":"loopback", "capture_endpoint":"must-not-open-a-capture-device", "duration_ms":5000,
                "gain":0.0, "timing":"ntp", "handshake_only":true})).unwrap();
            probe(options, Cancellation::default(), Arc::new(AtomicU32::new(0.0f32.to_bits())),
                |event| emitted.lock().unwrap().push(event)).unwrap();
            let volume_reads = server.join().unwrap();
            let emitted = emitted.into_inner().unwrap();
            let snapshot = emitted.iter().find(|event| event["event"] == "device_volume")
                .expect("silent handshake must read the current receiver volume");
            assert_eq!(volume_reads, 1);
            assert_eq!(snapshot["host"], "127.0.0.1");
            assert_eq!(snapshot["sequence"], 0);
            assert_eq!(snapshot["volume"], json!(expected));
            assert_eq!(snapshot["available"], expected.is_some());
            assert_eq!(snapshot["status"], if expected.is_some() { "confirmed" } else { "unsynced" });
            assert_eq!(snapshot["members"], json!([{"host":"127.0.0.1","volume":expected}]));
            assert_eq!(snapshot["message"].as_str().unwrap().is_empty(), expected.is_some());
            assert_eq!(emitted.last().unwrap()["event"], "handshake_complete");
            assert_eq!(emitted.last().unwrap()["audio_sent"], false);
            assert!(!emitted.iter().any(|event| event["event"] == "streaming" || event["event"] == "media_packet_sent"));
            for socket in [audio, control] {
                socket.set_nonblocking(true).unwrap();
                assert_eq!(socket.recv_from(&mut [0; 2048]).unwrap_err().kind(), std::io::ErrorKind::WouldBlock);
            }
        }
    }
    fn pair_server(c: &mut Connection, corrupt: bool) {
        let key = server_srp(c, "1234");
        let request = c.read().unwrap();
        let m5 = tlv_decode(&request.body).unwrap();
        assert_eq!(m5[&6], vec![5]);
        let session = derive(&key, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        let controller = tlv_decode(&auth_open(&session, b"PS-Msg05", &m5[&5]).unwrap()).unwrap();
        let controller_public: [u8; 32] = controller[&3].as_slice().try_into().unwrap();
        let x = derive(
            &key,
            "Pair-Setup-Controller-Sign-Salt",
            "Pair-Setup-Controller-Sign-Info",
        );
        VerifyingKey::from_bytes(&controller_public)
            .unwrap()
            .verify_strict(
                &[x.as_slice(), &controller[&1], &controller[&3]].concat(),
                &Signature::from_slice(&controller[&10]).unwrap(),
            )
            .unwrap();
        let signing = SigningKey::from_bytes(&[99; 32]);
        let public = signing.verifying_key().to_bytes();
        let id = b"synthetic-accessory";
        let x = derive(
            &key,
            "Pair-Setup-Accessory-Sign-Salt",
            "Pair-Setup-Accessory-Sign-Info",
        );
        let mut signature = signing
            .sign(&[x.as_slice(), id, &public].concat())
            .to_bytes();
        if corrupt {
            signature[0] ^= 1;
        }
        let encrypted = auth_seal(
            &session,
            b"PS-Msg06",
            &tlv_encode(&[(1, id), (3, &public), (10, &signature)]),
        )
        .unwrap();
        respond(c, &request, &tlv_encode(&[(6, &[6]), (5, &encrypted)]));
    }
    #[test]
    fn persistent_pair_requires_valid_accessory_signature() {
        for corrupt in [false, true] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let addr = listener.local_addr().unwrap();
            let server = thread::spawn(move || {
                let (socket, _) = listener.accept().unwrap();
                let mut conn = Connection::from_stream(socket, Cancellation::default()).unwrap();
                pair_server(&mut conn, corrupt);
            });
            let mut client = Connection::connect(addr, Cancellation::default()).unwrap();
            let result = pair(&mut client, "1234");
            assert_eq!(result.is_ok(), !corrupt);
            server.join().unwrap();
        }
    }
    #[test]
    fn persistent_verify_encrypts_control_and_rejects_identity_swap() {
        use x25519_dalek::{PublicKey, StaticSecret};
        for swap in [false, true] {
            let signing = SigningKey::from_bytes(&[99; 32]);
            let credentials = Credentials {
                accessory_id: b"synthetic-accessory".to_vec(),
                accessory_public: signing.verifying_key().to_bytes(),
                controller_id: b"test-controller".to_vec(),
                controller_secret: [77; 32],
            };
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let addr = listener.local_addr().unwrap();
            let server = thread::spawn(move || {
                let (socket, _) = listener.accept().unwrap();
                let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
                let request = c.read().unwrap();
                let m1 = tlv_decode(&request.body).unwrap();
                let secret = StaticSecret::from([55; 32]);
                let public = PublicKey::from(&secret);
                let client_public: [u8; 32] = m1[&3].as_slice().try_into().unwrap();
                let key = secret.diffie_hellman(&PublicKey::from(client_public));
                let session = derive(
                    key.as_bytes(),
                    "Pair-Verify-Encrypt-Salt",
                    "Pair-Verify-Encrypt-Info",
                );
                let id = if swap {
                    b"wrong-accessory".as_slice()
                } else {
                    b"synthetic-accessory".as_slice()
                };
                let signature = signing
                    .sign(&[public.as_bytes().as_slice(), id, &client_public].concat())
                    .to_bytes();
                let encrypted = auth_seal(
                    &session,
                    b"PV-Msg02",
                    &tlv_encode(&[(1, id), (10, &signature)]),
                )
                .unwrap();
                respond(
                    &mut c,
                    &request,
                    &tlv_encode(&[(6, &[2]), (3, public.as_bytes()), (5, &encrypted)]),
                );
                if swap {
                    return;
                }
                let request = c.read().unwrap();
                let m3 = tlv_decode(&request.body).unwrap();
                let verified =
                    tlv_decode(&auth_open(&session, b"PV-Msg03", &m3[&5]).unwrap()).unwrap();
                assert_eq!(verified[&1], b"test-controller");
                let key_public = SigningKey::from_bytes(&[77; 32]).verifying_key();
                key_public
                    .verify_strict(
                        &[client_public.as_slice(), &verified[&1], public.as_bytes()].concat(),
                        &Signature::from_slice(&verified[&10]).unwrap(),
                    )
                    .unwrap();
                respond(&mut c, &request, &tlv_encode(&[(6, &[4])]));
                c.encrypt(
                    derive(
                        key.as_bytes(),
                        "Control-Salt",
                        "Control-Read-Encryption-Key",
                    ),
                    derive(
                        key.as_bytes(),
                        "Control-Salt",
                        "Control-Write-Encryption-Key",
                    ),
                );
                let request = c.read().unwrap();
                assert!(request.first.starts_with("GET /info"));
                respond(&mut c, &request, b"encrypted-ok");
            });
            let mut c = Connection::connect(addr, Cancellation::default()).unwrap();
            let result = verify(&mut c, &credentials);
            assert_eq!(result.is_ok(), !swap);
            if !swap {
                assert_eq!(
                    c.request("GET", "/info", &[], &[]).unwrap().body,
                    b"encrypted-ok"
                );
            }
            server.join().unwrap();
        }
    }
}
