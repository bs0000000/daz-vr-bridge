// Wire framing shared with the Daz Studio plugin. See ../../../protocol/PROTOCOL.md.
//
//   u32   frame_len    bytes that follow this field (little-endian)
//   u32   header_len
//   u8[]  header       UTF-8 JSON object, always has "t" (type) and "seq"
//   u8[]  payload      frame_len - 4 - header_len bytes, often empty

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DazVrBridge
{
    public sealed class BridgeFrame
    {
        public const int ProtocolVersion = 1;
        public const int DefaultPort = 41427;
        public const uint MaxFrameBytes = 512u * 1024u * 1024u;

        public JObject Header;
        public byte[] Payload;

        public string Type => Header?.Value<string>("t");
        public long Seq => Header?.Value<long?>("seq") ?? -1;

        public static byte[] Encode(JObject header, byte[] payload = null)
        {
            var json = Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None));
            var payloadLen = payload?.Length ?? 0;
            var frameLen = 4 + json.Length + payloadLen;

            var buf = new byte[8 + frameLen];
            WriteU32(buf, 0, (uint)frameLen);
            WriteU32(buf, 4, (uint)json.Length);
            Buffer.BlockCopy(json, 0, buf, 8, json.Length);
            if (payloadLen > 0) Buffer.BlockCopy(payload, 0, buf, 8 + json.Length, payloadLen);
            return buf;
        }

        // Blocking read of exactly one frame. Returns null on a clean EOF.
        public static BridgeFrame Read(Stream stream)
        {
            var lenBytes = new byte[4];
            if (!ReadExactly(stream, lenBytes, 4)) return null;
            var frameLen = ReadU32(lenBytes, 0);
            if (frameLen < 4 || frameLen > MaxFrameBytes)
                throw new InvalidDataException($"bad frame length {frameLen}");

            var body = new byte[frameLen];
            if (!ReadExactly(stream, body, (int)frameLen))
                throw new EndOfStreamException("connection closed mid-frame");

            var headerLen = ReadU32(body, 0);
            if (headerLen > frameLen - 4)
                throw new InvalidDataException("header length exceeds frame");

            var json = Encoding.UTF8.GetString(body, 4, (int)headerLen);
            var payloadLen = (int)(frameLen - 4 - headerLen);
            var payload = new byte[payloadLen];
            if (payloadLen > 0) Buffer.BlockCopy(body, 4 + (int)headerLen, payload, 0, payloadLen);

            return new BridgeFrame { Header = JObject.Parse(json), Payload = payload };
        }

        static bool ReadExactly(Stream s, byte[] buf, int count)
        {
            var off = 0;
            while (off < count)
            {
                var n = s.Read(buf, off, count - off);
                if (n <= 0) return off == 0 ? false : throw new EndOfStreamException();
                off += n;
            }
            return true;
        }

        static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        static uint ReadU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }
    }
}
