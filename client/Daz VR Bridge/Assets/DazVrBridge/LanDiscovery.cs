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
        public sealed class Found
        {
            public string Address;      // what to dial
            public int Port;
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
        }

        const string Probe = "DAZVRBRIDGE?1";
        const string Answer = "DAZVRBRIDGE!1 ";
        /// An answer older than this is forgotten: Daz was closed, or the laptop left.
        const float Forget = 9f;

        public readonly List<Found> Servers = new List<Found>();
        /// Bumped whenever the list changes, so a UI can rebuild only when it must.
        public int Revision { get; private set; }

        readonly ConcurrentQueue<(IPAddress from, string json)> _answers =
            new ConcurrentQueue<(IPAddress, string)>();

        UdpClient _udp;
        int _port;
        bool _closed;

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
                    _answers.Enqueue((from.Address, text.Substring(Answer.Length)));
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
            var bytes = Encoding.UTF8.GetBytes(Probe);
            Send(bytes, IPAddress.Broadcast);

            // 255.255.255.255 is dropped by some stacks and some routers; the subnet's
            // own broadcast address usually survives where the global one does not, so
            // both go out and duplicate answers are merged by sender.
            foreach (var directed in DirectedBroadcasts()) Send(bytes, directed);
        }

        void Send(byte[] bytes, IPAddress to)
        {
            try { _udp.Send(bytes, bytes.Length, new IPEndPoint(to, _port)); }
            catch (Exception e) { LastError = e.Message; }
        }

        static IEnumerable<IPAddress> DirectedBroadcasts()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ip.IPv4Mask == null) continue;
                        var address = ip.Address.GetAddressBytes();
                        var mask = ip.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4) continue;
                        var b = new byte[4];
                        for (var i = 0; i < 4; i++) b[i] = (byte)(address[i] | ~mask[i]);
                        list.Add(new IPAddress(b));
                    }
                }
            }
            catch (Exception) { /* a platform without interface enumeration still has 255.255.255.255 */ }
            return list;
        }

        /// Main thread. Folds in whatever answered and forgets whoever stopped.
        public void Poll()
        {
            var changed = false;
            while (_answers.TryDequeue(out var answer))
                changed |= Merge(answer.from, answer.json);

            var now = Time.realtimeSinceStartup;
            for (var i = Servers.Count - 1; i >= 0; i--)
            {
                if (now - Servers[i].Seen <= Forget) continue;
                Servers.RemoveAt(i);
                changed = true;
            }
            if (changed) Revision++;
        }

        bool Merge(IPAddress from, string json)
        {
            JObject o;
            try { o = JObject.Parse(json); }
            catch (Exception) { return false; }

            var address = from.ToString();
            var port = o.Value<int?>("port") ?? BridgeFrame.DefaultPort;
            var found = Servers.Find(s => s.Address == address && s.Port == port);
            var isNew = found == null;
            if (isNew) { found = new Found { Address = address, Port = port }; Servers.Add(found); }

            var label = found.Label;
            found.Host = o.Value<string>("host");
            found.Scene = o.Value<string>("scene");
            found.Plugin = o.Value<string>("plugin");
            found.Pairing = o.Value<bool?>("pairing") ?? true;
            found.Clients = o.Value<int?>("clients") ?? 0;
            found.Seen = Time.realtimeSinceStartup;
            return isNew || label != found.Label;
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
