// The setup screen, on the monitor rather than in the headset.
//
// Two things put it here. A host address and a six-digit pairing code are typed, and
// typing in VR is miserable -- a keyboard floating in space is the worst part of most VR
// software, and this one has a perfectly good keyboard attached to it already.
//
// The second is a distinction the wheel cannot express. Everything on the wheel is live:
// toggle body contact, drag the clavicle share, see it happen. But textures, tex_max,
// influences, hulls and the region radius ride on scene.request and cost a full re-bake
// and re-download. Those are not settings in the same sense at all, and putting them
// beside a Connect button -- where changing one obviously means starting again -- says so
// better than any label could.
//
// Built in code rather than as a prefab so the repo stays a set of source files that can
// be read in a diff, which is how everything else here works.

using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DazVrBridge
{
    [DefaultExecutionOrder(-40)]
    public sealed class StartScreen : MonoBehaviour
    {
        const string Prefix = "DazVrBridge.Setup.";

        public BridgeSession session;
        public SceneLoader loader;
        // Off, and it stays off. A setup screen that connects before anyone has read it is
        // not a setup screen: the first version dialled on start with the saved settings,
        // and the scene was up and the screen gone before the headset was on. Everything
        // here is remembered, so the cost of waiting is one click on a button that is
        // already under the mouse -- and the click is what makes this the moment the
        // settings can be changed at all.
        [Tooltip("Dial as soon as the app starts, using the saved settings, instead of waiting for Connect.")]
        public bool autoConnect;

        Canvas _canvas;
        TMP_InputField _host, _port, _code, _texMax, _region;
        TMP_Dropdown _textures, _influences;
        Toggle _hulls;
        TextMeshProUGUI _status;
        Button _connect;
        bool _everConnected;

        static readonly Color Ground = new Color(0.062f, 0.078f, 0.098f, 1f);
        static readonly Color Panel = new Color(0.098f, 0.121f, 0.149f, 1f);
        static readonly Color FieldBg = new Color(0.145f, 0.176f, 0.212f, 1f);
        static readonly Color Ink = new Color(0.902f, 0.925f, 0.949f, 1f);
        static readonly Color Muted = new Color(0.565f, 0.627f, 0.682f, 1f);
        static readonly Color Accent = new Color(0.302f, 0.800f, 0.851f, 1f);

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            Load();
            Build();
            if (session) session.ControlState += _ => Refresh();
            Refresh();
            if (autoConnect) Apply();
        }

        void Update()
        {
            if (!_canvas) return;
            // Once a scene is up, the screen has done its job and the monitor is better
            // spent mirroring the headset. It comes back if the connection drops.
            var connected = session && session.ControlReady && loader && loader.Figures.Count > 0;
            if (connected) _everConnected = true;
            _canvas.gameObject.SetActive(!connected);
            if (!connected) Refresh();
        }

        // ---- settings

        void Load()
        {
            if (!session) return;
            session.host = PlayerPrefs.GetString(Prefix + "host", session.host);
            session.port = PlayerPrefs.GetInt(Prefix + "port", session.port);
            session.pairingCode = PlayerPrefs.GetString(Prefix + "code", session.pairingCode);
            if (!loader) return;
            loader.textures = PlayerPrefs.GetString(Prefix + "textures", loader.textures);
            loader.texMax = PlayerPrefs.GetInt(Prefix + "texMax", loader.texMax);
            loader.influences = PlayerPrefs.GetInt(Prefix + "influences", loader.influences);
            loader.hullProxies = PlayerPrefs.GetInt(Prefix + "hulls", loader.hullProxies ? 1 : 0) != 0;
            loader.regionRadius = PlayerPrefs.GetFloat(Prefix + "region", loader.regionRadius);
        }

        void Save()
        {
            if (session)
            {
                PlayerPrefs.SetString(Prefix + "host", session.host);
                PlayerPrefs.SetInt(Prefix + "port", session.port);
                PlayerPrefs.SetString(Prefix + "code", session.pairingCode);
            }
            Remember(loader);
        }

        /// The bake options, written where this screen will read them next time. Public
        /// because the in-VR panel sets the same five things, and a texture size chosen
        /// in the headset should still be there when the app is started from the desk.
        public static void Remember(SceneLoader loader)
        {
            if (loader)
            {
                PlayerPrefs.SetString(Prefix + "textures", loader.textures);
                PlayerPrefs.SetInt(Prefix + "texMax", loader.texMax);
                PlayerPrefs.SetInt(Prefix + "influences", loader.influences);
                PlayerPrefs.SetInt(Prefix + "hulls", loader.hullProxies ? 1 : 0);
                PlayerPrefs.SetFloat(Prefix + "region", loader.regionRadius);
            }
            PlayerPrefs.Save();
        }

        /// Take what is on screen and connect with it.
        public void Apply()
        {
            if (!session) return;
            session.host = string.IsNullOrWhiteSpace(_host.text) ? "127.0.0.1" : _host.text.Trim();
            session.port = int.TryParse(_port.text, out var port) && port > 0 ? port : BridgeFrame.DefaultPort;
            session.pairingCode = _code.text.Trim();

            if (loader)
            {
                loader.textures = _textures.options[_textures.value].text;
                loader.influences = _influences.value == 0 ? 4 : 8;
                loader.texMax = int.TryParse(_texMax.text, out var max) && max >= 64 ? max : 1024;
                loader.hullProxies = _hulls.isOn;
                loader.regionRadius = float.TryParse(_region.text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0f ? r : 0f;
            }

            Save();
            session.Reconnect();
            Refresh();
        }

        void Refresh()
        {
            if (!_status) return;
            var state = session ? session.Control.Current : BridgeClient.State.Disconnected;
            switch (state)
            {
                case BridgeClient.State.Connecting:
                    Set(Muted, $"Connecting to {session.host}:{session.port}...");
                    break;
                case BridgeClient.State.Connected:
                    Set(Accent, loader && loader.Busy ? loader.Status : "Connected. Waiting for the scene.");
                    break;
                case BridgeClient.State.Failed:
                    // Say what to do about it, not just that it went wrong.
                    var why = session.Control.LastError ?? "";
                    var hint = why.Contains("pairing") ? "Read the six digits from the VR Bridge pane in Daz."
                        : why.Contains("refused") || why.Length == 0 ? "Is Daz running, with the VR Bridge pane started?"
                        : "";
                    Set(new Color(1f, 0.45f, 0.4f), $"{why}\n{hint}".Trim());
                    break;
                default:
                    Set(Muted, _everConnected ? "Disconnected. Retrying." : "Ready. Press Connect.");
                    break;
            }
            if (_connect) _connect.interactable = state != BridgeClient.State.Connecting;
        }

        void Set(Color colour, string text)
        {
            if (_status.text != text) _status.text = text;
            _status.color = colour;
        }

        // ---- the screen itself

        void Build()
        {
            var go = new GameObject("Start screen", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 800f);
            scaler.matchWidthOrHeight = 0.5f;

            Fill(go.transform, Ground);

            var panel = Box(go.transform, "panel", Panel, new Vector2(520f, 640f));
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(36, 36, 32, 32);
            layout.spacing = 14f;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            Heading(panel, "Daz VR Bridge");
            Caption(panel, "The plugin's pane in Daz shows the address and the pairing code.");

            _host = Field(panel, "Daz machine", session ? session.host : "127.0.0.1");
            _port = Field(panel, "Port", (session ? session.port : BridgeFrame.DefaultPort).ToString());
            _code = Field(panel, "Pairing code", session ? session.pairingCode : "", "six digits; not needed on this machine");

            Caption(panel, "These decide what gets baked. Changing one means fetching the scene again.");
            _textures = Dropdown(panel, "Textures", new[] { "none", "opacity", "full" },
                loader ? Mathf.Max(0, System.Array.IndexOf(new[] { "none", "opacity", "full" }, loader.textures)) : 2);
            _texMax = Field(panel, "Texture size", (loader ? loader.texMax : 1024).ToString(), "longest edge in pixels");
            _influences = Dropdown(panel, "Bones per vertex", new[] { "4", "8" }, loader && loader.influences == 8 ? 1 : 0);
            _region = Field(panel, "Region radius", loader ? loader.regionRadius.ToString(CultureInfo.InvariantCulture) : "0",
                "cm around Daz's selection; 0 for the whole scene");
            _hulls = Check(panel, "Approximate strand hair", loader == null || loader.hullProxies);

            _status = Caption(panel, "");
            _status.fontSize = 15f;

            _connect = Action(panel, "Connect", Apply);
        }

        // ---- small builders, so the layout above reads as a layout

        static Image Fill(Transform parent, Color colour)
        {
            var go = new GameObject("ground", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.color = colour;
            return image;
        }

        static RectTransform Box(Transform parent, string name, Color colour, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = colour;
            return rect;
        }

        static void Heading(RectTransform parent, string text)
        {
            var t = Label(parent, text, 26f, Ink);
            t.fontStyle = FontStyles.Bold;
        }

        static TextMeshProUGUI Caption(RectTransform parent, string text)
        {
            var t = Label(parent, text, 14f, Muted);
            t.textWrappingMode = TextWrappingModes.Normal;
            return t;
        }

        static TextMeshProUGUI Label(RectTransform parent, string text, float size, Color colour)
        {
            var go = new GameObject("label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = colour;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            go.AddComponent<LayoutElement>().minHeight = size * 1.6f;
            return t;
        }

        TMP_InputField Field(RectTransform parent, string label, string value, string hint = null)
        {
            Label(parent, label, 13f, Muted).characterSpacing = 6f;

            var row = Box(parent, label + " field", FieldBg, new Vector2(0f, 34f));
            row.gameObject.AddComponent<LayoutElement>().minHeight = 34f;

            var textGo = new GameObject("text", typeof(RectTransform));
            textGo.transform.SetParent(row, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 16f;
            text.color = Ink;
            text.margin = new Vector4(10f, 4f, 10f, 4f);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;

            var input = row.gameObject.AddComponent<TMP_InputField>();
            input.textComponent = text;
            input.textViewport = textRect;
            input.text = value;
            input.lineType = TMP_InputField.LineType.SingleLine;
            if (!string.IsNullOrEmpty(hint)) Caption(parent, hint).fontSize = 12f;
            return input;
        }

        TMP_Dropdown Dropdown(RectTransform parent, string label, string[] options, int value)
        {
            Label(parent, label, 13f, Muted).characterSpacing = 6f;
            var row = Box(parent, label + " dropdown", FieldBg, new Vector2(0f, 34f));
            row.gameObject.AddComponent<LayoutElement>().minHeight = 34f;

            var caption = Label(row, options[Mathf.Clamp(value, 0, options.Length - 1)], 16f, Ink);
            caption.margin = new Vector4(10f, 0f, 10f, 0f);
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = Vector2.zero;
            captionRect.anchorMax = Vector2.one;
            captionRect.offsetMin = captionRect.offsetMax = Vector2.zero;

            var drop = row.gameObject.AddComponent<TMP_Dropdown>();
            drop.captionText = caption;
            drop.ClearOptions();
            foreach (var option in options) drop.options.Add(new TMP_Dropdown.OptionData(option));
            drop.value = Mathf.Clamp(value, 0, options.Length - 1);
            drop.RefreshShownValue();
            return drop;
        }

        Toggle Check(RectTransform parent, string label, bool value)
        {
            var row = Box(parent, label + " toggle", FieldBg, new Vector2(0f, 32f));
            row.gameObject.AddComponent<LayoutElement>().minHeight = 32f;

            var mark = Box(row, "mark", value ? Accent : Panel, new Vector2(16f, 16f));
            mark.anchorMin = mark.anchorMax = new Vector2(0f, 0.5f);
            mark.anchoredPosition = new Vector2(20f, 0f);

            var caption = Label(row, label, 15f, Ink);
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 1f);
            captionRect.offsetMin = new Vector2(40f, 0f);
            captionRect.offsetMax = Vector2.zero;

            var toggle = row.gameObject.AddComponent<Toggle>();
            toggle.isOn = value;
            toggle.targetGraphic = row.GetComponent<Image>();
            var markImage = mark.GetComponent<Image>();
            toggle.onValueChanged.AddListener(on => markImage.color = on ? Accent : Panel);
            return toggle;
        }

        Button Action(RectTransform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var row = Box(parent, label + " button", Accent, new Vector2(0f, 42f));
            row.gameObject.AddComponent<LayoutElement>().minHeight = 42f;

            var caption = Label(row, label, 17f, Ground);
            caption.alignment = TextAlignmentOptions.Center;
            caption.fontStyle = FontStyles.Bold;
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = Vector2.zero;
            captionRect.anchorMax = Vector2.one;
            captionRect.offsetMin = captionRect.offsetMax = Vector2.zero;

            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(onClick);
            return button;
        }
    }
}
