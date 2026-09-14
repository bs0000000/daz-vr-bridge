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
        static int _gcAtReset = -1;

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

            // Time is not the whole story: garbage is cheap to make and expensive to
            // collect, and a collection lands as a frame spike rather than as cost here.
            if (_gcAtReset >= 0)
            {
                var collections = System.GC.CollectionCount(0) - _gcAtReset;
                sb.Append($"  gen-0 collections  {collections,7}  ({(float)collections / _frames * 1000f:F1} per 1000 frames)\n");
            }
            sb.Append("  (anything the bridge does not do is Unity, XR submission, or waiting on the headset)");
            return sb.ToString();
        }

        public static void Reset()
        {
            Slots.Clear();
            _frames = 0;
            _gcAtReset = System.GC.CollectionCount(0);
        }
    }
}
