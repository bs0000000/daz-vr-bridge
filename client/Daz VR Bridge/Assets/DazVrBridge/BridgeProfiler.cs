// Where our own per-frame time goes. Unity's stats panel says how long the frame took;
// this says which part of the bridge spent it, which is the only way to tell an actual
// regression from a frame rate that is simply capped by the headset.
//
// Press P in play mode (PoseSync) for a breakdown.

using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DazVrBridge
{
    public static class BridgeProfiler
    {
        public static bool Enabled = true;

        struct Slot { public double Ms; public int Calls; }

        static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>();
        static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
        static int _frames;

        public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        public static void End(string name, long start)
        {
            if (!Enabled || start == 0L) return;
            var ms = (Stopwatch.GetTimestamp() - start) * ToMs;
            Slots.TryGetValue(name, out var slot);
            slot.Ms += ms;
            slot.Calls++;
            Slots[name] = slot;
        }

        public static void EndFrame() { if (Enabled) _frames++; }

        public static string Report()
        {
            if (_frames == 0) return "no frames measured";
            var sb = new StringBuilder($"[DazVrBridge] bridge cost over {_frames} frames\n");
            var total = 0.0;
            foreach (var kv in Slots)
            {
                total += kv.Value.Ms;
                sb.Append($"  {kv.Key,-18} {kv.Value.Ms / _frames,7:F3} ms/frame   {(float)kv.Value.Calls / _frames,6:F1} calls/frame\n");
            }
            sb.Append($"  {"TOTAL",-18} {total / _frames,7:F3} ms/frame\n");
            sb.Append("  (anything the bridge does not do is Unity, XR submission, or waiting on the headset)");
            return sb.ToString();
        }

        public static void Reset()
        {
            Slots.Clear();
            _frames = 0;
        }
    }
}
