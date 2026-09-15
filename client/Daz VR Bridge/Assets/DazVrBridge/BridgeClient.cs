// One TCP connection to the plugin (control or bulk). Reads on a background
// thread; frames are queued and handed to the main thread through Pump(),
// so everything that touches Unity objects stays on the main thread.
//
// The handshake, when the plugin wants one:
//
//   ->  hello        protocol, role, one credential (a saved token, or the six digits)
//   <-  welcome      an ephemeral RSA key, the plugin's nonce, and a MAC over both
//                    taken under that same credential
//   ->  secure.key   a random premaster encrypted to that key, and our nonce
//   <-  secure.ready first frame of the encrypted channel, carrying the next token
//
// Connected is not reported until secure.ready arrives, so nothing else in the client
// can send a frame into the gap where one side has switched and the other has not.

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BridgeClient : IDisposable
    {
        public enum State { Disconnected, Connecting, Connected, Failed }

        public State Current { get; private set; } = State.Disconnected;
        public string Role { get; }
        public string Session { get; private set; }
        public string LastError { get; private set; }
        public string LastErrorCode { get; private set; }
        /// True once every frame in both directions is encrypted.
        public bool Secure => _channel.Armed;
        /// A token the plugin issued for next time, picked up by BridgeSession and
        /// written to PlayerPrefs there: this thread must not touch Unity's API.
        public string IssuedToken { get; private set; }
        public int TokenDays { get; private set; }

        public event Action<BridgeFrame> FrameReceived; // main thread, via Pump()
        public event Action<State> StateChanged;         // main thread, via Pump()

        readonly ConcurrentQueue<BridgeFrame> _inbound = new ConcurrentQueue<BridgeFrame>();
        readonly ConcurrentQueue<State> _stateChanges = new ConcurrentQueue<State>();
        readonly object _sendLock = new object();

        TcpClient _tcp;
        NetworkStream _stream;
        Thread _reader;
        long _seq;
        readonly SecureChannel _channel = new SecureChannel();

        public BridgeClient(string role) { Role = role; }

        public void Connect(string host, int port, string pairingCode = null, string session = null,
                            string token = null)
        {
            Dispose();
            SetState(State.Connecting);

            _reader = new Thread(() => ReaderLoop(host, port, pairingCode, session, token))
            {
                IsBackground = true,
                Name = $"DazVrBridge-{Role}",
            };
            _reader.Start();
        }

        // Sends a frame. Safe from any thread. Fills in "seq".
        public long Send(JObject header, byte[] payload = null)
        {
            var seq = Interlocked.Increment(ref _seq);
            header["seq"] = seq;
            // Encoded and written under one lock. Sealing advances the channel's
            // counter, so two frames sealed in one order and written in the other
            // would be refused at the far end as out of order.
            lock (_sendLock)
            {
                try
                {
                    var bytes = BridgeFrame.Encode(header, payload, _channel);
                    _stream?.Write(bytes, 0, bytes.Length);
                }
                catch (Exception e) { Fail(e.Message); }
            }
            return seq;
        }

        public long Send(string type)
        {
            return Send(new JObject { ["t"] = type });
        }

        // Call once per Update() from a MonoBehaviour.
        public void Pump()
        {
            while (_stateChanges.TryDequeue(out var s)) StateChanged?.Invoke(s);
            while (_inbound.TryDequeue(out var f)) FrameReceived?.Invoke(f);
        }

        public void Dispose()
        {
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
            lock (_sendLock) _channel.Disarm();
            if (Current != State.Failed) SetState(State.Disconnected);
        }

        void ReaderLoop(string host, int port, string pairingCode, string session, string token)
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();

                // One credential, not both: whichever we send is also what the key
                // exchange is bound to, and the plugin cannot tell us which it picked.
                var haveToken = !string.IsNullOrEmpty(token);
                var secret = haveToken ? token : (pairingCode ?? "");

                var hello = new JObject
                {
                    ["t"] = "hello",
                    ["protocol"] = BridgeFrame.ProtocolVersion,
                    ["role"] = Role,
                    ["client"] = $"unity {Application.unityVersion}",
                    ["crypto"] = "v1",
                };
                if (haveToken) hello["token"] = token;
                else if (!string.IsNullOrEmpty(pairingCode)) hello["code"] = pairingCode;
                if (!string.IsNullOrEmpty(session)) hello["session"] = session;
                Send(hello);

                while (true)
                {
                    var frame = BridgeFrame.Read(_stream, _channel);
                    if (frame == null) break; // clean EOF

                    if (frame.Type == "welcome")
                    {
                        Session = frame.Header.Value<string>("session");
                        if (frame.Header.Value<string>("crypto") == "v1") Secure1(frame, secret);
                        else SetState(State.Connected);   // plaintext, by the plugin's choice
                    }
                    else if (frame.Type == "secure.ready")
                    {
                        IssuedToken = frame.Header.Value<string>("session_token");
                        TokenDays = frame.Header.Value<int?>("token_days") ?? 0;
                        SetState(State.Connected);
                    }
                    else if (frame.Type == "error" && Current != State.Connected)
                    {
                        // The plugin refused our hello; surface why.
                        LastError = frame.Header.Value<string>("msg");
                        LastErrorCode = frame.Header.Value<string>("code");
                    }
                    _inbound.Enqueue(frame);
                }

                if (Current == State.Connecting) Fail(LastError ?? "closed before welcome");
                else SetState(State.Disconnected);
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        // Second leg of the handshake. The MAC is the part that matters: anyone can
        // offer an RSA key, and only the plugin holding the same pairing code or token
        // can prove that this key is the one it meant to send.
        void Secure1(BridgeFrame welcome, string secret)
        {
            var modulus = Convert.FromBase64String(welcome.Header.Value<string>("key_n") ?? "");
            var exponent = Convert.FromBase64String(welcome.Header.Value<string>("key_e") ?? "");
            var serverNonce = Convert.FromBase64String(welcome.Header.Value<string>("nonce_s") ?? "");
            var mac = Convert.FromBase64String(welcome.Header.Value<string>("key_mac") ?? "");

            var binding = new byte[0];
            binding = Join(BridgeCrypto.Utf8("dazvrbridge key v1"), modulus, exponent, serverNonce);
            var expected = BridgeCrypto.HmacSha256(BridgeCrypto.Utf8(secret ?? ""), binding);
            if (mac.Length != expected.Length ||
                !BridgeCrypto.ConstantTimeEquals(expected, 0, mac, 0, expected.Length))
            {
                Fail("the plugin's key is not signed by that pairing code - someone may be in the middle");
                LastErrorCode = "bad_key_mac";
                try { _stream?.Close(); } catch { }
                return;
            }

            var premaster = BridgeCrypto.Random(32);
            var clientNonce = BridgeCrypto.Random(16);
            var sealed_ = BridgeCrypto.RsaOaepEncrypt(modulus, exponent, premaster);

            // Sent before arming, because this frame is the last one in the clear.
            Send(new JObject
            {
                ["t"] = "secure.key",
                ["k"] = Convert.ToBase64String(sealed_),
                ["nonce_c"] = Convert.ToBase64String(clientNonce),
                ["client"] = SystemInfo.deviceName,
            });
            lock (_sendLock) _channel.Arm(premaster, clientNonce, serverNonce, false);
            Array.Clear(premaster, 0, premaster.Length);
        }

        static byte[] Join(params byte[][] parts)
        {
            var total = 0;
            foreach (var p in parts) total += p.Length;
            var all = new byte[total];
            var at = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
            return all;
        }

        void Fail(string why)
        {
            LastError = why;
            SetState(State.Failed);
        }

        void SetState(State s)
        {
            if (Current == s) return;
            Current = s;
            _stateChanges.Enqueue(s);
        }
    }
}
