// Rig profile: which bones are grabbable, which are hidden, IK chains.
// Loaded from Resources/RigProfiles/<rig>.json (kept in sync with /profiles).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class RigProfile
    {
        public string Rig;
        public string Root;
        public HashSet<string> Grabbable = new HashSet<string>();
        public List<Regex> Hidden = new List<Regex>();
        public JObject Chains;
        public string LeftPrefix = "l_", RightPrefix = "r_";

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
            return p;
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
