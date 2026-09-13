// Rig profile: which bones are grabbable, which are hidden, IK chains.
// Loaded from Resources/RigProfiles/<rig>.json (kept in sync with /profiles).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    // One IK chain: grabbing End solves Root and Mid so End reaches the hand.
    public sealed class IkChain
    {
        public string Name;
        public string Root, Mid, End;
        public string Pole;             // "back" (elbows) | "front" (knees) | null
    }

    public sealed class RigProfile
    {
        public string Rig;
        public string Root;
        public HashSet<string> Grabbable = new HashSet<string>();
        public List<Regex> Hidden = new List<Regex>();
        public JObject Chains;
        public string LeftPrefix = "l_", RightPrefix = "r_";

        // The direction the figure faces, in Daz figure space (converted on use).
        public Vector3 ForwardDaz = new Vector3(0f, 0f, 1f);
        // IK chains by their end bone, so a handle can look itself up.
        public readonly Dictionary<string, IkChain> IkByEndBone = new Dictionary<string, IkChain>();

        static readonly Dictionary<string, RigProfile> Cache = new Dictionary<string, RigProfile>();

        public static RigProfile Load(string rig)
        {
            if (string.IsNullOrEmpty(rig)) rig = "unknown";
            if (Cache.TryGetValue(rig, out var p)) return p;

            var text = Resources.Load<TextAsset>($"RigProfiles/{rig}");
            if (text == null)
            {
                Debug.LogWarning($"[DazVrBridge] no rig profile for '{rig}'; every bone will be grabbable");
                p = new RigProfile { Rig = rig };
            }
            else
            {
                p = Parse(JObject.Parse(text.text));
            }
            Cache[rig] = p;
            return p;
        }

        static RigProfile Parse(JObject j)
        {
            var p = new RigProfile
            {
                Rig = j.Value<string>("rig"),
                Root = j.Value<string>("root"),
                Chains = j["chains"] as JObject,
            };
            foreach (var g in j["grabbable"] ?? new JArray()) p.Grabbable.Add(g.Value<string>());
            var hidden = j["hidden"]?["patterns"] as JArray;
            if (hidden != null)
                foreach (var h in hidden) p.Hidden.Add(GlobToRegex(h.Value<string>()));
            var mirror = j["mirror"] as JObject;
            if (mirror != null)
            {
                p.LeftPrefix = mirror.Value<string>("prefix_left") ?? p.LeftPrefix;
                p.RightPrefix = mirror.Value<string>("prefix_right") ?? p.RightPrefix;
            }

            var fwd = j["forward_daz"];
            if (fwd != null && fwd.Type == JTokenType.Array)
                p.ForwardDaz = new Vector3(fwd[0].Value<float>(), fwd[1].Value<float>(), fwd[2].Value<float>());

            if (p.Chains != null)
            {
                foreach (var entry in p.Chains)
                {
                    var chain = entry.Value as JObject;
                    if (chain == null || chain.Value<bool?>("ik") != true) continue;
                    var bones = chain["bones"] as JArray;
                    if (bones == null || bones.Count != 3) continue;
                    var ik = new IkChain
                    {
                        Name = entry.Key,
                        Root = bones[0].Value<string>(),
                        Mid = bones[1].Value<string>(),
                        End = bones[2].Value<string>(),
                        Pole = chain.Value<string>("pole"),
                    };
                    p.IkByEndBone[ik.End] = ik;
                }
            }
            return p;
        }

        // The figure's facing direction in world space. (Daz -> Unity mirrors Z.)
        public Vector3 ForwardOf(Transform figureRoot)
        {
            var local = new Vector3(ForwardDaz.x, ForwardDaz.y, -ForwardDaz.z);
            return figureRoot.TransformDirection(local).normalized;
        }

        // Where the elbow/knee should point when the limb is straight and there is no
        // bend plane to preserve.
        public Vector3 PoleDirection(IkChain chain, Transform figureRoot)
        {
            var fwd = ForwardOf(figureRoot);
            if (chain.Pole == "front") return fwd;
            if (chain.Pole == "back") return -fwd;
            return Vector3.zero;
        }

        public bool IsGrabbable(string boneId)
        {
            if (Grabbable.Count > 0) return Grabbable.Contains(boneId);
            foreach (var re in Hidden) if (re.IsMatch(boneId)) return false;
            return true;
        }

        public string Mirror(string boneId)
        {
            if (boneId.StartsWith(LeftPrefix)) return RightPrefix + boneId.Substring(LeftPrefix.Length);
            if (boneId.StartsWith(RightPrefix)) return LeftPrefix + boneId.Substring(RightPrefix.Length);
            return null;
        }

        static Regex GlobToRegex(string glob)
        {
            return new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        }
    }
}
