// Finding the plugin without being told where it is.
//
// One short line goes out as a UDP broadcast, and every Daz on the network that is
// listening answers with its port, its scene and whether it will want a pairing code.
// The answer's SENDER address is what gets dialled -- the hostname it reports is a
// label, because a machine name that does not resolve is worse than useless when the
// whole point is to save someone typing an address.
//
// Probes rather than beacons: an idle Daz puts nothing on the network until somebody
// is actually looking for it.
//
// One Daz answers on every interface it has, so the same machine arrives two or three
// times -- loopback, the real LAN, and whatever address a container runtime left on the
// box. They are folded together by the instance id the plugin sends, and the address
// that gets dialled is the best ROUTE to that instance: loopback first, because it is
// literally this machine; then an address on a network of ours that has a gateway;
// then one on a network without one, which is what a docker bridge looks like; and
// round-trip time breaks any tie.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class LanDiscovery : IDisposable
    {
        /// One way to reach an instance. Several of these usually mean one machine.
        public sealed class Route
        {
            public string Address;
            public int Rank;            // lower is better; see Rank() below
            public double Rtt;          // milliseconds, last answer
            public float Seen;
        }

        public sealed class Found
        {
            public string Id;           // the plugin's instance id, when it sends one
            public string Address;      // the best route, which is what gets dialled
            public int Port;
            public readonly List<Route> Routes = new List<Route>();
            public string Host;         // what to show
            public string Scene;
            public string Plugin;
            public bool Pairing;
            public int Clients;
            public float Seen;          // Time.realtimeSinceStartup of the last answer

            public string Label
            {
                get
                {
                    var name = string.IsNullOrEmpty(Host) ? Address : Host;
                    if (!string.IsNullOrEmpty(Scene)) name += "  -  " + Scene;
                    return name;
                }
            }

            /// The address that will actually be dialled, and how many ways there were
            /// to reach it. Worth showing: "three of the same machine" was confusing,
            /// and so is one entry with no clue which of its addresses won.
            public string Detail =>
                Routes.Count > 1 ? Address + "  (" + Routes.Count + " routes)" : Address;
        }

        const string Probe = "DAZVRBRIDGE?1";
        const string Answer = "DAZVRBRIDGE!1 ";
        /// An answer older than this is forgotten: Daz was closed, or the laptop left.
        const float Forget = 9f;

        public readonly List<Found> Servers = new List<Found>();
        /// Bumped whenever the list changes, so a UI can rebuild only when it must.
        public int Revision { get; private set; }

        readonly ConcurrentQueue<(IPAddress from, string json, double rtt)> _answers =
            new ConcurrentQueue<(IPAddress, string, double)>();

        UdpClient _udp;
        int _port;
        bool _closed;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        long _askedAt;              // _clock ticks, written by Ask, read by the receiver
        Local[] _local = new Local[0];
        float _localUntil;

        struct Local
        {
            public uint Network;    // address & mask
            public uint Mask;
            public bool Gateway;    // a network with a way out, rather than a bridge
        }

        public string LastError { get; private set; }

        public void Start(int port)
        {
            Dispose();
            _closed = false;
            _port = port > 0 ? port : BridgeFrame.DefaultPort;
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                Receive();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                _udp = null;
            }
        }

        void Receive()
        {
            if (_udp == null || _closed) return;
            try
            {
                _udp.BeginReceive(OnReceived, _udp);
            }
            catch (Exception e) { LastError = e.Message; }
        }

        // Background thread: parse nothing that touches Unity, just queue the text.
        void OnReceived(IAsyncResult result)
        {
            if (_closed) return;
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var bytes = ((UdpClient)result.AsyncState).EndReceive(result, ref from);
                var text = Encoding.UTF8.GetString(bytes);
                if (text.StartsWith(Answer, StringComparison.Ordinal))
                {
                    var rtt = (_clock.ElapsedTicks - System.Threading.Interlocked.Read(ref _askedAt))
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    _answers.Enqueue((from.Address, text.Substring(Answer.Length), rtt));
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { LastError = e.Message; }
            Receive();
        }

        /// Asks who is out there. Cheap enough to call every couple of seconds while
        /// somebody is looking at a list of servers, and never otherwise.
        public void Ask(int port)
        {
            if (_udp == null) return;
            _port = port > 0 ? port : BridgeFrame.DefaultPort;
            System.Threading.Interlocked.Exchange(ref _askedAt, _clock.ElapsedTicks);
            var bytes = Encoding.UTF8.GetBytes(Probe);
            Send(bytes, IPAddress.Broadcast);
            // Loopback does not hear a 255.255.255.255 broadcast on every stack, and a
            // Daz on this very machine is the one answer most worth having.
            Send(bytes, IPAddress.Loopback);

            // 255.255.255.255 is dropped by some stacks and some routers; the subnet's
            // own broadcast address usually survives where the global one does not, so
            // both go out and the duplicate answers are folded together below.
            foreach (var directed in DirectedBroadcasts()) Send(bytes, directed);
        }

        void Send(byte[] bytes, IPAddress to)
        {
            try { _udp.Send(bytes, bytes.Length, new IPEndPoint(to, _port)); }
            catch (Exception e) { LastError = e.Message; }
        }

        IEnumerable<IPAddress> DirectedBroadcasts()
        {
            var list = new List<IPAddress>();
            foreach (var local in Locals())
            {
                var broadcast = local.Network | ~local.Mask;
                list.Add(new IPAddress(new[]
                {
                    (byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast,
                }));
            }
            return list;
        }

        // This machine's IPv4 networks, re-read every few seconds: a VPN or a container
        // can appear mid-session, and enumerating interfaces is not free enough to do
        // twice a second.
        Local[] Locals()
        {
            if (Time.realtimeSinceStartup < _localUntil) return _local;
            _localUntil = Time.realtimeSinceStartup + 5f;

            var list = new List<Local>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var properties = nic.GetIPProperties();
                    var gateway = false;
                    foreach (var g in properties.GatewayAddresses)
                        if (g.Address != null && !g.Address.Equals(IPAddress.Any)) { gateway = true; break; }

                    foreach (var ip in properties.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ip.IPv4Mask == null) continue;
                        var address = ToUint(ip.Address);
                        var mask = ToUint(ip.IPv4Mask);
                        if (mask == 0) continue;
                        list.Add(new Local { Network = address & mask, Mask = mask, Gateway = gateway });
                    }
                }
            }
            catch (Exception) { /* a platform without interface enumeration still has 255.255.255.255 */ }
            _local = list.ToArray();
            return _local;
        }

        static uint ToUint(IPAddress address)
        {
            var b = address.GetAddressBytes();
            if (b.Length != 4) return 0;
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        // Lower is better. Loopback is this machine and cannot be beaten; then a network
        // of ours that has a gateway, which is a real one; then a network of ours that
        // does not, which is what a container bridge or a host-only adapter looks like;
        // then anything we cannot place at all.
        int Rank(IPAddress from)
        {
            if (IPAddress.IsLoopback(from)) return 0;
            var address = ToUint(from);
            var best = 3;
            foreach (var local in Locals())
            {
                if ((address & local.Mask) != local.Network) continue;
                best = Mathf.Min(best, local.Gateway ? 1 : 2);
            }
            return best;
        }

        /// Main thread. Folds in whatever answered and forgets whoever stopped.
        public void Poll()
        {
            var changed = false;
            while (_answers.TryDequeue(out var answer))
                changed |= Merge(answer.from, answer.json, answer.rtt);

            var now = Time.realtimeSinceStartup;
            for (var i = Servers.Count - 1; i >= 0; i--)
            {
                if (now - Servers[i].Seen <= Forget) continue;
                Servers.RemoveAt(i);
                changed = true;
            }
            if (changed) Revision++;
        }

        bool Merge(IPAddress from, string json, double rtt)
        {
            JObject o;
            try { o = JObject.Parse(json); }
            catch (Exception) { return false; }

            var address = from.ToString();
            var port = o.Value<int?>("port") ?? BridgeFrame.DefaultPort;
            var id = o.Value<string>("id");
            var host = o.Value<string>("host");

            // By instance id where there is one, so one Daz answering on three
            // interfaces is one entry. A plugin too old to send an id folds by machine
            // name and port instead, which is the same machine often enough to be worth
            // having and cannot merge two Daz instances -- they cannot share a port.
            var found = !string.IsNullOrEmpty(id)
                ? Servers.Find(s => s.Id == id)
                : !string.IsNullOrEmpty(host)
                    ? Servers.Find(s => s.Id == null && s.Host == host && s.Port == port)
                    : Servers.Find(s => s.Id == null && s.Address == address && s.Port == port);
            var isNew = found == null;
            if (isNew) { found = new Found { Id = id, Port = port }; Servers.Add(found); }

            var was = found.Address;
            var label = found.Label;
            var now = Time.realtimeSinceStartup;

            var route = found.Routes.Find(r => r.Address == address);
            if (route == null) { route = new Route { Address = address }; found.Routes.Add(route); }
            route.Rank = Rank(from);
            route.Rtt = rtt;
            route.Seen = now;

            // A route that has stopped answering is not a way to reach anything.
            found.Routes.RemoveAll(r => now - r.Seen > Forget);
            found.Routes.Sort((a, b) => a.Rank != b.Rank ? a.Rank - b.Rank : a.Rtt.CompareTo(b.Rtt));
            found.Address = found.Routes.Count > 0 ? found.Routes[0].Address : address;
            found.Port = port;

            found.Host = host;
            found.Scene = o.Value<string>("scene");
            found.Plugin = o.Value<string>("plugin");
            found.Pairing = o.Value<bool?>("pairing") ?? true;
            found.Clients = o.Value<int?>("clients") ?? 0;
            found.Seen = now;
            return isNew || label != found.Label || was != found.Address;
        }

        public void Dispose()
        {
            _closed = true;
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null;
            Servers.Clear();
        }
    }
}
