// Owns the connection(s) to the plugin for the whole app: one control
// connection (always) and one bulk connection (opened once the control
// session exists). Other components subscribe to frames here instead of
// opening sockets of their own.

using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BridgeSession : MonoBehaviour
    {
        [Header("Plugin (from the VR Bridge pane)")]
        public string host = "127.0.0.1";
        public int port = BridgeFrame.DefaultPort;
        [Tooltip("Only needed when the plugin runs on another machine.")]
        public string pairingCode = "";

        public BridgeClient Control { get; private set; }
        public BridgeClient Bulk { get; private set; }

        public bool ControlReady => Control != null && Control.Current == BridgeClient.State.Connected;
        public bool BulkReady => Bulk != null && Bulk.Current == BridgeClient.State.Connected;

        public event Action<BridgeFrame> ControlFrame;
        public event Action<BridgeFrame> BulkFrame;
        public event Action<BridgeClient.State> ControlState;
        public event Action<BridgeClient.State> BulkState;

        const float PingInterval = 2f;
        const float ReconnectDelay = 3f;

        float _nextPing;
        float _nextReconnect;

        void Awake()
        {
            Control = new BridgeClient("control");
            Control.StateChanged += OnControlState;
            Control.FrameReceived += f => ControlFrame?.Invoke(f);

            Bulk = new BridgeClient("bulk");
            Bulk.StateChanged += s => BulkState?.Invoke(s);
            Bulk.FrameReceived += f => BulkFrame?.Invoke(f);

            // Undo/redo rides along with the connection rather than being wired into the
            // scene, so an existing scene gets it without being rebuilt. Added in Awake so
            // it is subscribed before the first Pump() delivers the welcome. Drop an
            // EditSync in the scene yourself if you want to change the bindings.
            if (!FindAnyObjectByType<EditSync>()) gameObject.AddComponent<EditSync>();
            if (!FindAnyObjectByType<StartScreen>()) gameObject.AddComponent<StartScreen>();
            if (!FindAnyObjectByType<DeskSync>()) gameObject.AddComponent<DeskSync>();
            if (!FindAnyObjectByType<BindingLabels>()) gameObject.AddComponent<BindingLabels>();
            if (!FindAnyObjectByType<PoseTakes>()) gameObject.AddComponent<PoseTakes>();
            if (!FindAnyObjectByType<VrMenu>()) gameObject.AddComponent<VrMenu>();
            if (!FindAnyObjectByType<VrPanel>()) gameObject.AddComponent<VrPanel>();
            if (!FindAnyObjectByType<PanelMenu>()) gameObject.AddComponent<PanelMenu>();
        }

        void Start()
        {
            // The start screen connects when it is ready, so it can apply saved settings
            // first rather than have a connection race the fields that configure it.
            if (!FindAnyObjectByType<StartScreen>()) ConnectControl();
        }

        /// Drop whatever is open and dial again with the current host, port and code.
        public void Reconnect()
        {
            Bulk?.Dispose();
            Control?.Dispose();
            ConnectControl();
        }


        void OnDestroy()
        {
            Bulk?.Dispose();
            Control?.Dispose();
        }

        void Update()
        {
            Control.Pump();
            Bulk.Pump();

            switch (Control.Current)
            {
                case BridgeClient.State.Connected:
                    if (Time.time >= _nextPing)
                    {
                        Control.Send("ping");
                        _nextPing = Time.time + PingInterval;
                    }
                    // Bulk follows the control session; open it when we have one.
                    if (Bulk.Current == BridgeClient.State.Disconnected || Bulk.Current == BridgeClient.State.Failed)
                    {
                        if (Time.time >= _nextReconnect) ConnectBulk();
                    }
                    break;

                case BridgeClient.State.Disconnected:
                case BridgeClient.State.Failed:
                    if (Time.time >= _nextReconnect) ConnectControl();
                    break;
            }
        }

        public long SendControl(JObject header, byte[] payload = null) => Control.Send(header, payload);
        public long SendBulk(JObject header, byte[] payload = null) => Bulk.Send(header, payload);

        void ConnectControl()
        {
            _nextReconnect = Time.time + ReconnectDelay;
            Bulk.Dispose(); // a new control session invalidates the old bulk one
            Control.Connect(host, port, pairingCode);
        }

        void ConnectBulk()
        {
            _nextReconnect = Time.time + ReconnectDelay;
            Bulk.Connect(host, port, pairingCode, Control.Session);
        }

        void OnControlState(BridgeClient.State s)
        {
            if (s == BridgeClient.State.Connected) _nextReconnect = 0f; // open bulk right away
            ControlState?.Invoke(s);
        }
    }
}
