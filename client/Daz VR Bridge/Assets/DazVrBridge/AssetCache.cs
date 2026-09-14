// Content-addressed disk cache for baked assets. A resync only transfers
// hashes that are not already here, which is what makes the two-machine
// setup bearable over Wi-Fi.

using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace DazVrBridge
{
    public static class AssetCache
    {
        static string Root => Path.Combine(Application.persistentDataPath, "bridge-cache");

        static string PathFor(string hash)
        {
            // "sha1:<hex>" -> "<hex>.bin"; refuse anything that is not a plain hex hash
            var hex = hash.StartsWith("sha1:") ? hash.Substring(5) : hash;
            foreach (var c in hex)
                if (!Uri.IsHexDigit(c)) throw new ArgumentException($"bad asset hash '{hash}'");
            return Path.Combine(Root, hex + ".bin");
        }

        public static bool TryGet(string hash, out byte[] bytes)
        {
            bytes = null;
            try
            {
                var path = PathFor(hash);
                if (!File.Exists(path)) return false;
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DazVrBridge] cache read failed for {hash}: {e.Message}");
                return false;
            }
        }

        // Verifies the content hash before storing; a mismatch is a transport bug.
        //
        // `verify: false` is for assets whose hash names their SOURCE rather than their
        // bytes -- textures, whose id is the map files they were converted from, so that
        // the manifest can list them without decoding anything. There is nothing to check
        // those against here; the plugin checks the produced size against what it
        // promised, which catches the same class of mistake.
        public static bool Put(string hash, byte[] bytes, bool verify = true)
        {
            if (verify && !Verify(hash, bytes))
            {
                Debug.LogError($"[DazVrBridge] asset {hash} failed hash verification ({bytes.Length} bytes)");
                return false;
            }
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllBytes(PathFor(hash), bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DazVrBridge] cache write failed for {hash}: {e.Message}");
            }
            return true;
        }

        public static bool Verify(string hash, byte[] bytes)
        {
            if (!hash.StartsWith("sha1:")) return true; // unknown scheme: trust it
            using (var sha = SHA1.Create())
            {
                var hex = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                return hex == hash.Substring(5).ToLowerInvariant();
            }
        }
    }
}
