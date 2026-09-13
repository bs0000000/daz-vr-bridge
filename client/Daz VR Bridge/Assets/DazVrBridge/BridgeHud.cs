// Status text: connection state, the open Daz scene, and load progress.
// Put on a world-space TextMeshPro object; assign the BridgeSession.

using System.IO;
using TMPro;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BridgeHud : MonoBehaviour
    {
        public BridgeSession session;
        public SceneLoader loader;   // optional
        public PoseSync poseSync;    // optional
        public BoneHandles handles;  // optional
        public TMP_Text text;

        string _dazVersion = "";
        string _scenePath = "";
        int _sceneNodes;

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            if (!poseSync) poseSync = FindAnyObjectByType<PoseSync>();
            if (!handles) handles = FindAnyObjectByType<BoneHandles>();
            if (session)
            {
                session.ControlFrame += OnFrame;
                session.ControlState += _ => Render();
            }
            Render();
        }

        string _lastSelfTest = "";
        string _lastHands = "";
        string _lastLimits = "";
        VrRig _rig;
        float _nextHandsCheck;

        void Update()
        {
            if (loader && loader.Busy) Render();
            if (poseSync && poseSync.LastSelfTest != _lastSelfTest) { _lastSelfTest = poseSync.LastSelfTest; Render(); }

            if (Time.time >= _nextHandsCheck)
            {
                _nextHandsCheck = Time.time + 0.1f; // fast enough to watch buttons
                var hands = HandsStatus();
                var limits = handles ? handles.LimitText : "";
                if (hands != _lastHands || limits != _lastLimits) { _lastHands = hands; _lastLimits = limits; Render(); }
            }
        }

        string HandsStatus()
        {
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();
            if (!_rig || !_rig.Left || !_rig.Right) return "";
            string One(VrHand h) => h.IsTracked ? $"tracked ({h.DeviceName}) {h.ButtonMonitor()}" : (h.DeviceName.Length > 0 ? $"seen, not tracked ({h.DeviceName})" : "no device");
            return $"L: {One(_rig.Left)}\nR: {One(_rig.Right)}\nrig scale {_rig.Scale:F2}  devices: {VrHand.DescribeDevices()}";
        }

        void OnFrame(BridgeFrame f)
        {
            switch (f.Type)
            {
                case "welcome":
                    _dazVersion = f.Header.Value<string>("daz_version");
                    ReadScene(f.Header["scene"]);
                    break;
                case "scene.changed":
                    ReadScene(f.Header["scene"]);
                    break;
                case "error":
                    Debug.LogWarning($"[DazVrBridge] {f.Header.Value<string>("code")}: {f.Header.Value<string>("msg")}");
                    break;
            }
            Render();
        }

        void ReadScene(Newtonsoft.Json.Linq.JToken scene)
        {
            _scenePath = scene?.Value<string>("path") ?? "";
            _sceneNodes = scene?.Value<int?>("nodes") ?? 0;
        }

        void Render()
        {
            if (!text || !session) return;

            switch (session.Control.Current)
            {
                case BridgeClient.State.Connecting:
                    text.text = $"Connecting to {session.host}:{session.port}…";
                    break;
                case BridgeClient.State.Connected:
                    var name = string.IsNullOrEmpty(_scenePath) ? "(unsaved scene)" : Path.GetFileName(_scenePath);
                    var status = loader ? "\n" + loader.Status : "";
                    var selfTest = poseSync && poseSync.LastSelfTest.Length > 0 ? "\n" + poseSync.LastSelfTest : "";
                    var hands = _lastHands.Length > 0 ? "\n<size=70%>" + _lastHands + "</size>" : "";
                    var limits = _lastLimits.Length > 0 ? "\n<color=#FF6050>" + _lastLimits + "</color>" : "";
                    text.text = $"<b>{name}</b>\n{_sceneNodes} nodes · Daz Studio {_dazVersion}{status}{selfTest}{limits}{hands}";
                    break;
                case BridgeClient.State.Failed:
                    text.text = $"Failed: {session.Control.LastError}\nretrying…";
                    break;
                default:
                    text.text = "Disconnected";
                    break;
            }
        }
    }
}
