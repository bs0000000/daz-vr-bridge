// The client half of the encrypted channel. Mirrors plugin/crypto.cpp byte for byte.
//
// The primitive choices are forced by the intersection of what Qt/CNG and Unity's Mono
// both do without shipping a library: RSA-OAEP key transport, AES-256-CBC records,
// HMAC-SHA256 in encrypt-then-MAC order. Nothing here is invented -- it is TLS 1.2's
// construction assembled from parts that are certain to exist at both ends.
//
// OAEP uses SHA-1 for its mask, deliberately. Mono's RSA only guarantees the fOAEP
// overload, and OAEP's security argument treats the hash as a mask generator rather
// than relying on collision resistance, so SHA-1 there is not the weakness SHA-1
// signatures are. A padding the runtime refuses at 3am in a headset would be.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DazVrBridge
{
    public static class BridgeCrypto
    {
        public static byte[] Random(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            using (var mac = new HMACSHA256(key)) return mac.ComputeHash(data);
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var hash = SHA256.Create()) return hash.ComputeHash(data);
        }

        /// RFC 5869. One shared secret becomes four independent keys.
        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            var prk = HmacSha256(salt, ikm);
            var output = new byte[length];
            var block = new byte[0];
            var filled = 0;
            byte counter = 1;
            while (filled < length)
            {
                var input = new byte[block.Length + info.Length + 1];
                Buffer.BlockCopy(block, 0, input, 0, block.Length);
                Buffer.BlockCopy(info, 0, input, block.Length, info.Length);
                input[input.Length - 1] = counter++;
                block = HmacSha256(prk, input);
                var take = Math.Min(block.Length, length - filled);
                Buffer.BlockCopy(block, 0, output, filled, take);
                filled += take;
            }
            return output;
        }

        public static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (var e = aes.CreateEncryptor()) return e.TransformFinalBlock(plain, 0, plain.Length);
            }
        }

        public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher, int offset, int count)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (var d = aes.CreateDecryptor()) return d.TransformFinalBlock(cipher, offset, count);
            }
        }

        /// Length-independent, so a wrong MAC cannot be found one byte at a time.
        public static bool ConstantTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            if (a.Length < aOffset + count || b.Length < bOffset + count) return false;
            var diff = 0;
            for (var i = 0; i < count; i++) diff |= a[aOffset + i] ^ b[bOffset + i];
            return diff == 0;
        }

        public static byte[] RsaOaepEncrypt(byte[] modulus, byte[] exponent, byte[] data)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                return rsa.Encrypt(data, true);   // OAEP-SHA1, the overload Mono always has
            }
        }

        public static string ToBase64Url(byte[] raw) =>
            Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    }

    /// One connection's record layer. See plugin/crypto.h for the record's shape; the
    /// two implementations have to agree to the byte, and a test harness checks that
    /// they do rather than leaving it to the first evening in a headset.
    public sealed class SecureChannel
    {
        static readonly byte[] Tag = { (byte)'D', (byte)'Z', (byte)'E', 0x01 };
        public const int MinRecord = 4 + 8 + 16 + 16 + 32;

        public bool Armed { get; private set; }

        byte[] _sendKey, _sendMac, _recvKey, _recvMac;
        ulong _sendCounter, _recvCounter;

        public void Arm(byte[] premaster, byte[] clientNonce, byte[] serverNonce, bool asServer)
        {
            var salt = new byte[clientNonce.Length + serverNonce.Length];
            Buffer.BlockCopy(clientNonce, 0, salt, 0, clientNonce.Length);
            Buffer.BlockCopy(serverNonce, 0, salt, clientNonce.Length, serverNonce.Length);

            var toServer = BridgeCrypto.Hkdf(premaster, salt, BridgeCrypto.Utf8("dazvrbridge v1 client to server"), 64);
            var toClient = BridgeCrypto.Hkdf(premaster, salt, BridgeCrypto.Utf8("dazvrbridge v1 server to client"), 64);

            var send = asServer ? toClient : toServer;
            var recv = asServer ? toServer : toClient;
            _sendKey = Slice(send, 0, 32); _sendMac = Slice(send, 32, 32);
            _recvKey = Slice(recv, 0, 32); _recvMac = Slice(recv, 32, 32);
            _sendCounter = 0;
            _recvCounter = 0;
            Armed = true;
        }

        public void Disarm()
        {
            Armed = false;
            _sendKey = _sendMac = _recvKey = _recvMac = null;
        }

        public byte[] Seal(byte[] body)
        {
            if (!Armed) throw new InvalidOperationException("channel is not armed");
            var iv = BridgeCrypto.Random(16);
            var cipher = BridgeCrypto.AesCbcEncrypt(_sendKey, iv, body);

            var record = new byte[4 + 8 + 16 + cipher.Length + 32];
            Buffer.BlockCopy(Tag, 0, record, 0, 4);
            WriteU64(record, 4, ++_sendCounter);
            Buffer.BlockCopy(iv, 0, record, 12, 16);
            Buffer.BlockCopy(cipher, 0, record, 28, cipher.Length);

            var signed = new byte[record.Length - 32];
            Buffer.BlockCopy(record, 0, signed, 0, signed.Length);
            Buffer.BlockCopy(BridgeCrypto.HmacSha256(_sendMac, signed), 0, record, signed.Length, 32);
            return record;
        }

        public byte[] Open(byte[] record)
        {
            if (!Armed) throw new InvalidOperationException("channel is not armed");
            if (record.Length < MinRecord) throw new InvalidDataException("short record");
            for (var i = 0; i < 4; i++)
                if (record[i] != Tag[i]) throw new InvalidDataException("not an encrypted record");

            var macAt = record.Length - 32;
            var signed = new byte[macAt];
            Buffer.BlockCopy(record, 0, signed, 0, macAt);
            var expected = BridgeCrypto.HmacSha256(_recvMac, signed);
            if (!BridgeCrypto.ConstantTimeEquals(expected, 0, record, macAt, 32))
                throw new InvalidDataException("record failed its MAC");

            var counter = ReadU64(record, 4);
            if (counter <= _recvCounter) throw new InvalidDataException("record replayed or out of order");
            _recvCounter = counter;

            // Only after the MAC passed: nothing unauthenticated reaches a cipher, let
            // alone a JSON parser.
            var iv = Slice(record, 12, 16);
            return BridgeCrypto.AesCbcDecrypt(_recvKey, iv, record, 28, macAt - 28);
        }

        static byte[] Slice(byte[] source, int offset, int count)
        {
            var b = new byte[count];
            Buffer.BlockCopy(source, offset, b, 0, count);
            return b;
        }

        static void WriteU64(byte[] b, int o, ulong v)
        {
            for (var i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        static ulong ReadU64(byte[] b, int o)
        {
            ulong v = 0;
            for (var i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }
    }
}
