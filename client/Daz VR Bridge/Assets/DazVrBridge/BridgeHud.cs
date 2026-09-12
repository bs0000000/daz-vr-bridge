// Phase 0 exit criterion: put this on a world-space TextMeshPro object, enter
// the plugin's address, put on the headset, and read the open scene's name.
//
// Keeps a control connection alive with pings, reconnects with backoff, and
// mirrors scene.changed so the text follows what Daz Studio has open.

using System.IO;
using TMPro;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BridgeHud : MonoBehaviour
    {
        [Header("Plugin (from the VR Bridge pane)")]
        public string host = "127.0.0.1";
        public int port = BridgeFrame.DefaultPort;
        [Tooltip("Only needed when the plugin runs on another machine.")]
        public string pairingCode = "";

        [Header("Output")]
        public TMP_Text text;

        const float PingInterval = 2f;
        const float ReconnectDelay = 3f;

        BridgeClient _control;
        float _nextPing;
        float _nextReconnect;
        string _dazVersion = "";
        string _scenePath = "";
        int _sceneNodes;

        void Start()
        {
            _control = new BridgeClient("control");
            _control.StateChanged += OnState;
            _control.FrameReceived += OnFrame;
            Connect();
        }

        void OnDestroy()
        {
            _control?.Dispose();
        }

        void Update()
        {
            _control.Pump();

            switch (_control.Current)
            {
                case BridgeClient.State.Connected:
                    if (Time.time >= _nextPing)
                    {
                        _control.Send("ping");
                        _nextPing = Time.time + PingInterval;
                    }
                    break;

                case BridgeClient.State.Disconnected:
                case BridgeClient.State.Failed:
                    if (Time.time >= _nextReconnect) Connect();
                    break;
            }
        }

        void Connect()
        {
            _nextReconnect = Time.time + ReconnectDelay;
            _control.Connect(host, port, pairingCode);
        }

        void OnState(BridgeClient.State s)
        {
            Render();
        }

        void OnFrame(BridgeFrame f)
        {
            switch (f.Type)
            {
                case "welcome":
                    _dazVersion = f.Header.Value<string>("daz_version");
                    ReadScene(f);
                    break;

                case "scene.changed":
                    ReadScene(f);
                    break;

                case "error":
                    Debug.LogWarning($"[DazVrBridge] {f.Header.Value<string>("code")}: {f.Header.Value<string>("msg")}");
                    break;
            }
            Render();
        }

        void ReadScene(BridgeFrame f)
        {
            var scene = f.Header["scene"];
            _scenePath = scene?.Value<string>("path") ?? "";
            _sceneNodes = scene?.Value<int?>("nodes") ?? 0;
        }

        void Render()
        {
            if (!text) return;

            switch (_control.Current)
            {
                case BridgeClient.State.Connecting:
                    text.text = $"Connecting to {host}:{port}…";
                    break;
                case BridgeClient.State.Connected:
                    var name = string.IsNullOrEmpty(_scenePath) ? "(unsaved scene)" : Path.GetFileName(_scenePath);
                    text.text = $"<b>{name}</b>\n{_sceneNodes} nodes\nDaz Studio {_dazVersion}";
                    break;
                case BridgeClient.State.Failed:
                    text.text = $"Failed: {_control.LastError}\nretrying…";
                    break;
                default:
                    text.text = "Disconnected";
                    break;
            }
        }
    }
}
