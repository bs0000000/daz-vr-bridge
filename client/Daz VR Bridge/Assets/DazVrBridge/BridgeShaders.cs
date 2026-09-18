// Shaders that exist in a player, not only in the editor.
//
// Shader.Find searches what was BUILT. In the editor that is every shader in the project,
// so it always works; in a player it is only what something in a built scene referenced,
// plus whatever is listed in Graphics settings. A URP shader that is only ever named in a
// string at runtime is in neither list, so Shader.Find returns null, and `new Material(null)`
// is either a magenta object or an exception -- and an exception in the middle of building
// a scene takes the rest of the scene with it.
//
// This is the second time that has bitten: the handle overlay shader went into Resources
// for the same reason. So the lookups live here, they never return null, and they say so
// loudly once when they have had to fall back -- because a silently wrong material is how
// this cost an evening.

using UnityEngine;

namespace DazVrBridge
{
    public static class BridgeShaders
    {
        static Shader _lit, _unlit;
        static bool _warned;

        /// A lit surface shader. URP's Lit where it survived into the build.
        public static Shader Lit()
        {
            if (_lit) return _lit;
            _lit = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Fallback();
            return _lit;
        }

        /// An unlit shader, for anything that carries its own image: a camera's
        /// picture-in-picture, a readout, a marker.
        public static Shader Unlit()
        {
            if (_unlit) return _unlit;
            _unlit = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Texture")
                ?? Fallback();
            return _unlit;
        }

        /// Makes a material without ever handing a null shader to the constructor.
        public static Material Material(Shader shader, string name)
        {
            var material = new Material(shader ? shader : Fallback()) { name = name };
            return material;
        }

        // The overlay shader is in Resources, so it is in every build by construction.
        // It is the wrong look for a figure and the right answer for not crashing.
        static Shader Fallback()
        {
            var shader = Resources.Load<Shader>("HandleOverlay");
            if (!_warned)
            {
                _warned = true;
                Debug.LogWarning(shader
                    ? "[DazVrBridge] A shader was missing from this build and the overlay shader is standing in. " +
                      "Add Universal Render Pipeline/Lit and /Unlit to Project Settings > Graphics > Always Included Shaders."
                    : "[DazVrBridge] No shader could be found at all, including the overlay shader in Resources. " +
                      "Materials will render magenta.");
            }
            return shader;
        }
    }
}
