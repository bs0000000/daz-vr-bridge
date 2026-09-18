// Shaders that exist in a player, not only in the editor.
//
// Shader.Find searches what was BUILT. In the editor that is every shader in the project,
// so it always works; in a player it is only what something in a built scene referenced,
// plus whatever is listed in Graphics settings. A URP shader that is only ever named in a
// string at runtime is in neither list, so Shader.Find returns null -- and a null shader
// is a magenta object, or an exception that takes the rest of the scene build with it.
//
// The fix is a material ASSET under Resources. Anything in Resources ships by
// construction, the material carries the shader with it, and -- unlike a line in
// ProjectSettings/GraphicsSettings.asset -- it is a file Unity imports when it appears
// rather than a setting a running editor holds in memory and writes back over the top of.
// That distinction cost a build: the always-included entries were on disk and the player
// was still missing the shader.
//
// Third time for this class of bug. The handle overlay went into Resources for the same
// reason, so that is where the rest of them live now too.

using UnityEngine;

namespace DazVrBridge
{
    public static class BridgeShaders
    {
        static Material _lit, _unlit;
        static bool _warned;

        /// A lit surface material, fresh each call. URP's Lit.
        public static Material Lit(string name) => Clone(ref _lit, "BridgeLit", name, false);

        /// An unlit material, for anything carrying its own image: a camera's
        /// picture-in-picture, a readout, a marker.
        public static Material Unlit(string name) => Clone(ref _unlit, "BridgeUnlit", name, true);

        /// For the few places that genuinely want a Shader rather than a Material.
        public static Shader LitShader()
        {
            var template = Template(ref _lit, "BridgeLit", false);
            return template ? template.shader : Fallback();
        }

        static Material Clone(ref Material cached, string resource, string name, bool unlit)
        {
            var template = Template(ref cached, resource, unlit);
            return template
                ? new Material(template) { name = name }
                : new Material(Fallback()) { name = name };
        }

        static Material Template(ref Material cached, string resource, bool unlit)
        {
            if (cached) return cached;

            // The asset first, because it is the one that survives a build.
            cached = Resources.Load<Material>(resource);
            if (cached && cached.shader) return cached;

            // Then the name, which works in the editor and in a build where something
            // else happened to pull the shader in.
            var shader = Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit")
                      ?? Shader.Find(unlit ? "Unlit/Texture" : "Standard");
            if (shader) cached = new Material(shader) { name = resource };
            else Warn(resource);
            return cached;
        }

        // The overlay shader is in Resources too, so it is in every build. It has one
        // colour and no texture, which is the wrong look for anything and the right
        // answer for not crashing -- a grey rectangle where a camera preview should be
        // is this fallback, doing its job.
        static Shader Fallback()
        {
            var shader = Resources.Load<Shader>("HandleOverlay");
            if (!shader) Debug.LogError("[DazVrBridge] No shader at all, including the overlay shader in Resources.");
            return shader;
        }

        static void Warn(string resource)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning(
                $"[DazVrBridge] {resource}.mat is missing from Resources and the shader could not be found by name, " +
                "so the overlay shader is standing in: expect flat grey where a texture should be. " +
                "Check that Assets/DazVrBridge/Resources/" + resource + ".mat imported.");
        }
    }
}
