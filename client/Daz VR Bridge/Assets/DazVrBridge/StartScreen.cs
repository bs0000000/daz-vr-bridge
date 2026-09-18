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

        /// True while the screen is up on the monitor waiting for somebody. The headset
        /// cannot show a screen-space canvas, so the HUD says where to look instead.
        public bool Waiting => _canvas && _canvas.gameObject.activeSelf;

        Canvas _canvas;
        TMP_InputField _host, _port, _code, _texMax, _region;
        Segment _textures, _influences;
        RectTransform _foundList;
        LanDiscovery _discovery;
        int _foundRevision = -1;
        float _nextProbe;
        Toggle _hulls;
        TextMeshProUGUI _status;
        Button _connect;
        bool _everConnected;
        bool _touchedHost;      // typed an address: stop filling it in from discovery
        static readonly string[] TextureNames = { "none", "opacity", "full" };
        LanDiscovery.Found _picked;

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
            _discovery = new LanDiscovery();
            _discovery.Start(session ? session.port : BridgeFrame.DefaultPort);
            Refresh();
            if (autoConnect) Apply();
        }

        void OnDestroy()
        {
            _discovery?.Dispose();
        }

        void Update()
        {
            if (!_canvas) return;
            // Once a scene is up, the screen has done its job and the monitor is better
            // spent mirroring the headset. It comes back if the connection drops.
            var connected = session && session.ControlReady && loader && loader.Figures.Count > 0;
            if (connected) _everConnected = true;
            _canvas.gameObject.SetActive(!connected);
            if (connected) return;
            Refresh();
            Look();
        }

        // ---- who is out there
        //
        // Nobody should have to read an IP address off another machine's screen to use
        // this. One broadcast every couple of seconds while this screen is up, and every
        // Daz that is listening puts itself in the list.
        void Look()
        {
            if (_discovery == null) return;
            if (Time.unscaledTime >= _nextProbe)
            {
                _nextProbe = Time.unscaledTime + 2f;
                _discovery.Ask(int.TryParse(_port.text, out var port) ? port : BridgeFrame.DefaultPort);
            }
            _discovery.Poll();
            if (_discovery.Revision == _foundRevision) return;
            _foundRevision = _discovery.Revision;

            var servers = _discovery.Servers;

            // One answer and there is nothing to choose between: fill it in. Two or more
            // and the choice is the user's, because guessing which Daz they meant is how
            // someone ends up posing a scene on the wrong machine.
            if (servers.Count == 1 && !_touchedHost) Take(servers[0]);

            // Detached before being destroyed: Destroy happens at the end of the frame,
            // and a layout group counts children that are still parented to it, so the
            // old rows would push the new ones down for one frame on every change.
            for (var i = _foundList.childCount - 1; i >= 0; i--)
            {
                var child = _foundList.GetChild(i);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }

            if (servers.Count == 0)
            {
                Caption(_foundList, "looking...");
                return;
            }
            foreach (var server in servers)
            {
                var found = server;
                Entry(_foundList, found.Label, found == _picked, () =>
                {
                    Take(found);
                    _touchedHost = false;
                    _foundRevision = -1;    // repaint, so the tick moves to this one
                });
            }
        }

        void Take(LanDiscovery.Found server)
        {
            _host.SetTextWithoutNotify(server.Address);
            _port.SetTextWithoutNotify(server.Port.ToString());
            _picked = server;
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
                loader.textures = TextureNames[_textures.Value];
                loader.influences = _influences.Value == 0 ? 4 : 8;
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
                    var how = session.Secure ? "Connected, encrypted." : "Connected, in the clear.";
                    Set(Accent, loader && loader.Busy ? loader.Status : how + " Waiting for the scene.");
                    break;
                case BridgeClient.State.Failed:
                    // Say what to do about it, not just that it went wrong.
                    var why = session.Control.LastError ?? "";
                    var hint = why.Contains("pairing") ? "Read the six digits from the VR Bridge pane in Daz."
                        : why.Contains("in the middle") ? "The code does not match the key that answered. Check the six digits, and that this is the machine you meant."
                        : why.Contains("encrypted connections") ? "That Daz is newer than this client, or this client needs rebuilding."
                        : why.Contains("refused") || why.Length == 0 ? "Is Daz running, with the VR Bridge pane started?"
                        : "";
                    Set(new Color(1f, 0.45f, 0.4f), $"{why}\n{hint}".Trim());
                    break;
                default:
                    if (_everConnected) Set(Muted, "Disconnected. Retrying.");
                    else if (_picked != null)
                        Set(Muted, _picked.Pairing
                            ? $"{_picked.Label} is listening, and wants its pairing code."
                            : $"{_picked.Label} is listening. No code needed.");
                    else if (session && session.HasSavedSession)
                        Set(Muted, "Ready. This machine is paired, so no code is needed.");
                    else Set(Muted, "Ready. Press Connect.");
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

            // Two columns, and the panel's height follows its contents rather than being
            // a number I picked. A single column grew past the bottom of the screen the
            // moment discovery and un-pairing were added to it, and a settings screen
            // whose settings are off the screen is not a settings screen.
            var panel = Box(go.transform, "panel", Panel, new Vector2(880f, 0f));
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(34, 34, 28, 28);
            layout.spacing = 12f;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Heading(panel, "Daz VR Bridge");

            var columns = Row(panel, 26f);
            var left = Column(columns);
            var right = Column(columns);

            Caption(left, "The VR Bridge pane in Daz shows the address and the code.");
            Label(left, "Found on the network", 13f, Muted).characterSpacing = 6f;
            _foundList = Column(left);

            _host = Field(left, "Daz machine", session ? session.host : "127.0.0.1");
            _host.onValueChanged.AddListener(_ => _touchedHost = true);
            _port = Field(left, "Port", (session ? session.port : BridgeFrame.DefaultPort).ToString());
            _code = Field(left, "Pairing code (six digits)", session ? session.pairingCode : "");

            Caption(right, "What gets baked. Changing one means fetching the scene again.");
            _textures = Segmented(right, "Textures", TextureNames,
                loader ? Mathf.Max(0, System.Array.IndexOf(TextureNames, loader.textures)) : 2);
            _texMax = Field(right, "Texture size (px)", (loader ? loader.texMax : 1024).ToString());
            _influences = Segmented(right, "Bones per vertex", new[] { "4", "8" }, loader && loader.influences == 8 ? 1 : 0);
            _region = Field(right, "Region radius (cm, 0 = all)",
                loader ? loader.regionRadius.ToString(CultureInfo.InvariantCulture) : "0");
            _hulls = Check(right, "Approximate strand hair", loader == null || loader.hullProxies);

            _status = Caption(panel, "");
            _status.fontSize = 15f;

            var buttons = Row(panel, 12f);
            _connect = Action(Column(buttons), "Connect", Apply);
            // Un-pairing, from the side that holds the token. The other side of the
            // same coin is "Forget paired" in the VR Bridge pane, which drops every
            // headset at once by changing the key the tokens are signed with.
            Action(Column(buttons), "Forget the saved session", () =>
            {
                session?.ForgetSession();
                Refresh();
            });
        }

        // A row of equal columns. Everything else here stacks; these two do not.
        static RectTransform Row(RectTransform parent, float spacing)
        {
            var go = new GameObject("row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;
            return (RectTransform)go.transform;
        }

        static RectTransform Column(RectTransform parent)
        {
            var go = new GameObject("column", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 9f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return (RectTransform)go.transform;
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
            // Sentences, unlike field labels, are longer than the column they sit in.
            // They wrap, and the layout takes their height from what wrapping produced.
            t.textWrappingMode = TextWrappingModes.Normal;
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

        // A row of chips, one lit. Not a TMP_Dropdown: that needs a template prefab with
        // a Toggle in it, and a screen built in code has none, so every dropdown here
        // logged an error and opened nothing at all the moment it was clicked. A handful
        // of options never needed a popup anyway -- they fit on the screen, and a control
        // that shows every choice at once is a better control than one that hides them.
        sealed class Segment
        {
            public int Value { get; private set; }

            readonly Image[] _chips;
            readonly TextMeshProUGUI[] _texts;

            public Segment(Image[] chips, TextMeshProUGUI[] texts) { _chips = chips; _texts = texts; }

            public void Set(int value)
            {
                Value = Mathf.Clamp(value, 0, _chips.Length - 1);
                for (var i = 0; i < _chips.Length; i++)
                {
                    _chips[i].color = i == Value ? Accent : FieldBg;
                    _texts[i].color = i == Value ? Ground : Ink;
                }
            }
        }

        Segment Segmented(RectTransform parent, string label, string[] options, int value)
        {
            Label(parent, label, 13f, Muted).characterSpacing = 6f;
            var row = Row(parent, 6f);
            var chips = new Image[options.Length];
            var texts = new TextMeshProUGUI[options.Length];
            Segment segment = null;

            for (var i = 0; i < options.Length; i++)
            {
                var index = i;
                var chip = Box(row, options[i] + " chip", FieldBg, new Vector2(0f, 34f));
                chip.gameObject.AddComponent<LayoutElement>().minHeight = 34f;
                texts[i] = Stretch(Label(chip, options[i], 15f, Ink));
                texts[i].alignment = TextAlignmentOptions.Center;
                chips[i] = chip.GetComponent<Image>();
                var button = chip.gameObject.AddComponent<Button>();
                button.targetGraphic = chips[i];
                button.onClick.AddListener(() => segment.Set(index));
            }

            segment = new Segment(chips, texts);
            segment.Set(value);
            return segment;
        }

        // One discovered machine. Rebuilt whenever the list changes, so it is a button
        // rather than anything with state of its own.
        void Entry(RectTransform parent, string text, bool chosen, UnityEngine.Events.UnityAction onClick)
        {
            var row = Box(parent, "found", chosen ? Accent : FieldBg, new Vector2(0f, 32f));
            row.gameObject.AddComponent<LayoutElement>().minHeight = 32f;
            var caption = Stretch(Label(row, text, 15f, chosen ? Ground : Ink));
            caption.margin = new Vector4(10f, 0f, 10f, 0f);
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(onClick);
        }

        static TextMeshProUGUI Stretch(TextMeshProUGUI text)
        {
            var rect = (RectTransform)text.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return text;
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
