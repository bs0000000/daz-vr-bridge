// One TCP connection to the plugin (control or bulk). Reads on a background
// thread; frames are queued and handed to the main thread through Pump(),
// so everything that touches Unity objects stays on the main thread.

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

        public event Action<BridgeFrame> FrameReceived; // main thread, via Pump()
        public event Action<State> StateChanged;         // main thread, via Pump()

        readonly ConcurrentQueue<BridgeFrame> _inbound = new ConcurrentQueue<BridgeFrame>();
        readonly ConcurrentQueue<State> _stateChanges = new ConcurrentQueue<State>();
        readonly object _sendLock = new object();

        TcpClient _tcp;
        NetworkStream _stream;
        Thread _reader;
        long _seq;

        public BridgeClient(string role) { Role = role; }

        public void Connect(string host, int port, string pairingCode = null, string session = null)
        {
            Dispose();
            SetState(State.Connecting);

            _reader = new Thread(() => ReaderLoop(host, port, pairingCode, session))
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
            var bytes = BridgeFrame.Encode(header, payload);
            lock (_sendLock)
            {
                try { _stream?.Write(bytes, 0, bytes.Length); }
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
            if (Current != State.Failed) SetState(State.Disconnected);
        }

        void ReaderLoop(string host, int port, string pairingCode, string session)
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();

                var hello = new JObject
                {
                    ["t"] = "hello",
                    ["protocol"] = BridgeFrame.ProtocolVersion,
                    ["role"] = Role,
                    ["client"] = $"unity {Application.unityVersion}",
                };
                if (!string.IsNullOrEmpty(pairingCode)) hello["code"] = pairingCode;
                if (!string.IsNullOrEmpty(session)) hello["session"] = session;
                Send(hello);

                while (true)
                {
                    var frame = BridgeFrame.Read(_stream);
                    if (frame == null) break; // clean EOF

                    if (frame.Type == "welcome")
                    {
                        Session = frame.Header.Value<string>("session");
                        SetState(State.Connected);
                    }
                    else if (frame.Type == "error" && Current != State.Connected)
                    {
                        // The plugin refused our hello; surface why.
                        LastError = frame.Header.Value<string>("msg");
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
