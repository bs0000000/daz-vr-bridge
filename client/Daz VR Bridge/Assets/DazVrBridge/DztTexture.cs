// A texture asset, already in the form the GPU wants it.
//
//   "DZT1"  u32 format (1 = BC1, 3 = BC3)  u32 width  u32 height  u32 mips
//   then every mip level, largest first, back to back
//
// The plugin does the decode, the scale, the mip chain and the block compression, so
// everything here is a header read and one copy into a Texture2D. That is deliberate:
// decoding a 4096-square JPEG or calling Texture2D.Compress costs hundreds of
// milliseconds on the main thread, and there is no frame in VR that can absorb it.

using System;
using UnityEngine;

namespace DazVrBridge
{
    public static class DztTexture
    {
        const int HeaderBytes = 20;

        public static Texture2D Build(byte[] bytes, string name)
        {
            if (bytes == null || bytes.Length < HeaderBytes) return null;
            if (bytes[0] != 'D' || bytes[1] != 'Z' || bytes[2] != 'T' || bytes[3] != '1') return null;

            var format = BitConverter.ToUInt32(bytes, 4);
            var width = (int)BitConverter.ToUInt32(bytes, 8);
            var height = (int)BitConverter.ToUInt32(bytes, 12);
            var mips = (int)BitConverter.ToUInt32(bytes, 16);
            if (width <= 0 || height <= 0 || mips <= 0) return null;

            var texture = new Texture2D(width, height,
                format == 3 ? TextureFormat.DXT5 : TextureFormat.DXT1, mips, linear: false)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                // Skin at a glancing angle is the worst case in VR, and trilinear alone
                // turns it to mush; the mip chain is already paid for.
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };

            var payload = bytes.Length - HeaderBytes;
            var expected = texture.GetRawTextureData<byte>().Length;
            if (payload != expected)
            {
                Debug.LogWarning($"[DazVrBridge] {name}: texture is {payload} bytes, Unity wants {expected} " +
                    $"({width}x{height}, {mips} mips, {(format == 3 ? "DXT5" : "DXT1")})");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            // One copy to strip the header, then the upload. Texture loads are rare and
            // never happen while a hand is moving, so a transient array is the right
            // trade against pinning the buffer.
            var raw = new byte[payload];
            Buffer.BlockCopy(bytes, HeaderBytes, raw, 0, payload);
            texture.LoadRawTextureData(raw);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }
    }
}
