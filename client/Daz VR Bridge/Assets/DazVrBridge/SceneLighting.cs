// Light of the client's own, so a build is never a dark room.
//
// The Unity scene has no lights in it. Nothing lit the figures except two things that
// both turn out to be conditional: whatever lights the Daz scene happened to bring, and
// the ambient probe baked from the skybox. A Daz scene lit by an HDRI environment -- which
// is most of them -- brings no lights at all, and the baked probe is editor-fresh in the
// editor and whatever shipped in the player. Between them, "it looked fine yesterday" and
// "everything is dark in the build" are the same scene.
//
// So: set ambient explicitly at runtime, and carry a three-point rig that switches itself
// on when the Daz scene brought nothing. Runtime lights cannot be stripped from a build
// and need no bake, which is the point -- this is about being deterministic, not about
// being pretty.
//
// The rig is fixed in the scene rather than carried on the head. A light that follows you
// is a headlamp: it removes every shadow that tells you where a hand is in relation to a
// body, which is the one thing this tool exists to judge.

using UnityEngine;
using UnityEngine.Rendering;

namespace DazVrBridge
{
    public sealed class SceneLighting : MonoBehaviour
    {
        public enum Fill
        {
            Auto,       // only when the Daz scene brought no lights of its own
            Always,     // on top of whatever Daz brought
            Never,      // the Daz scene's lights, or nothing
        }

        [Tooltip("When the client lights the scene itself.")]
        public Fill fill = Fill.Auto;

        [Tooltip("Ambient level. Even at zero the rig keeps a figure readable; this is what fills the shadows.")]
        [Range(0f, 1.5f)] public float ambient = 0.55f;

        [Tooltip("The key light's strength. Fill and rim follow it.")]
        [Range(0f, 3f)] public float key = 1.1f;

        // A neutral daylight-ish set, biased slightly warm from above and cool from below,
        // which is what makes skin read as skin rather than as grey plastic.
        static readonly Color Sky = new Color(0.58f, 0.62f, 0.70f);
        static readonly Color Equator = new Color(0.45f, 0.45f, 0.47f);
        static readonly Color Ground = new Color(0.25f, 0.23f, 0.21f);

        SceneLoader _loader;
        Transform _rig;
        Light _key, _side, _rim;

        /// True when the client's own lights are doing the work.
        public bool Lighting => _rig && _rig.gameObject.activeSelf;

        void Start()
        {
            _loader = FindAnyObjectByType<SceneLoader>();
            if (_loader) _loader.SceneBuilt += Apply;
            Build();
            Apply();
        }

        void OnDestroy()
        {
            if (_loader) _loader.SceneBuilt -= Apply;
            if (_rig) Destroy(_rig.gameObject);
        }

        /// Re-reads the scene and sets the lighting to match. Cheap; call it after
        /// anything that might have changed either.
        public void Apply()
        {
            // Explicit, every time. Skybox ambient needs a baked probe, and a baked probe
            // is exactly the thing that was there in the editor and missing in the build.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Sky * ambient;
            RenderSettings.ambientEquatorColor = Equator * ambient;
            RenderSettings.ambientGroundColor = Ground * ambient;

            if (!_rig) Build();
            var wanted = fill == Fill.Always || (fill == Fill.Auto && DazLights() == 0);
            _rig.gameObject.SetActive(wanted);
            if (!wanted) return;

            _key.intensity = key;
            _side.intensity = key * 0.45f;
            _rim.intensity = key * 0.6f;
        }

        /// How many lights the Daz scene brought. Zero is the common case: an Iray scene
        /// lit by an environment has nothing that survives as a Unity light.
        int DazLights()
        {
            if (!_loader) return 0;
            var count = 0;
            foreach (var node in _loader.Nodes.Values)
                if (node.Type == "light" && SceneLoader.IsNodeVisible(node)) count++;
            return count;
        }

        void Build()
        {
            if (_rig) return;
            var go = new GameObject("Bridge lighting");
            _rig = go.transform;
            _rig.SetParent(transform, false);

            // Three points, the arrangement a photographer would set up: a key high and
            // to one side, a softer fill opposite it to open the shadows, and a rim from
            // behind so a figure separates from whatever is behind it.
            _key = Make("key", new Vector3(50f, 35f, 0f), new Color(1f, 0.97f, 0.92f));
            _side = Make("fill", new Vector3(20f, -60f, 0f), new Color(0.88f, 0.92f, 1f));
            _rim = Make("rim", new Vector3(-10f, 165f, 0f), new Color(0.95f, 0.97f, 1f));
        }

        Light Make(string name, Vector3 euler, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_rig, false);
            go.transform.localRotation = Quaternion.Euler(euler);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            // No shadows. A key light with soft shadows would help judge where a hand is,
            // and would also be the first thing to cost frames on a 144 Hz target; that is
            // a trade worth making deliberately rather than by default.
            light.shadows = LightShadows.None;
            return light;
        }
    }
}
