using CommunicationLibrary.Models;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;

namespace Broadcaster.SignalR
{
    [HubName("livehub")]
    public class SignalR : Hub
    {
        // Mapeia screenShareId -> hostConnectionId (opcionalmente)
        private static ConcurrentDictionary<string, string> HostByScreenId = new ConcurrentDictionary<string, string>();

        // Mapeia connectionId -> set of screenShareIds (para limpeza ao desconectar)
        private static ConcurrentDictionary<string, ConcurrentBag<string>> ConnectionGroups = new ConcurrentDictionary<string, ConcurrentBag<string>>();

        public SignalR()
        {
        }

        // -------------------------
        // PUBLIC API (broadcaster calls)
        // -------------------------
        // Broadcaster sends generic input (keyboard/mouse) to group (all viewers)
        public async Task Produce(InputDataComm data, string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            try
            {
                // envia para todos os membros do grupo, exceto o caller por padrão
                await Clients.Group(screenShareId).Produce(data);
            }
            catch (Exception ex)
            {
                // Log e swallow - não deixa o hub cair
                DebugWrite($"Produce error: {ex.Message}");
            }
        }

        public async Task ProduceMouseMove(int x, int y, string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            try
            {
                await Clients.Group(screenShareId).ProduceMouseMove(x, y);
            }
            catch (Exception ex)
            {
                DebugWrite($"ProduceMouseMove error: {ex.Message}");
            }
        }

        public async Task ProduceScreenshot(byte[] data, string width, string height, string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;
            if (data == null) return;

            try
            {
                // Envia para todos no grupo (viewers). Se quiser excluir o host que envia, mantenha como Group.
                await Clients.Group(screenShareId).ProduceScreenshot(data, width, height);
            }
            catch (Exception ex)
            {
                DebugWrite($"ProduceScreenshot error: {ex.Message}");
            }
        }

        public async Task StopScreenShare(string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            try
            {
                await Clients.Group(screenShareId).StopScreenShare();
            }
            catch (Exception ex)
            {
                DebugWrite($"StopScreenShare error: {ex.Message}");
            }
        }

        // -------------------------
        // CONNECTION / REGISTRY API
        // -------------------------
        // Called by the BROADCASTER to register itself as the host for a given screenShareId
        public async Task RegisterHost(string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            var connectionId = Context.ConnectionId;
            HostByScreenId.AddOrUpdate(screenShareId, connectionId, (k, old) => connectionId);

            // Add connection to group so host can receive group messages if needed
            await Groups.Add(connectionId, screenShareId);
            AddConnectionGroup(connectionId, screenShareId);

            // Notify host that registration succeeded
            try
            {
                await Clients.Client(connectionId).ClientConnected(true);
            }
            catch { }
        }

        // Called by a VIEWER to join a screenShare room
        public async Task JoinScreenShare(string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            var connectionId = Context.ConnectionId;
            await Groups.Add(connectionId, screenShareId);
            AddConnectionGroup(connectionId, screenShareId);

            // Optionally respond to viewer with success
            try
            {
                await Clients.Client(connectionId).ClientConnected(true);
            }
            catch { }
        }

        // Viewer can leave a given screenShare room
        public async Task LeaveScreenShare(string screenShareId)
        {
            if (string.IsNullOrEmpty(screenShareId)) return;

            var connectionId = Context.ConnectionId;
            await Groups.Remove(connectionId, screenShareId);
            RemoveConnectionGroup(connectionId, screenShareId);
            try
            {
                await Clients.Client(connectionId).ClientConnected(false);
            }
            catch { }
        }

        // Legacy compatibility (keeps old RegisterClient signature but make it join group)
        public async Task RegisterClient(string hubId, string screenShareId)
        {
            // here hubId param is ignored; we use Context.ConnectionId
            await JoinScreenShare(screenShareId);
        }

        // TryConnect / AuthenticateSuccess - route to group or host as needed
        public async Task TryConnect(string username, string password, string screenShareId)
        {
            // If you need to send this only to the host, use HostByScreenId
            if (HostByScreenId.TryGetValue(screenShareId, out var hostConn))
            {
                try
                {
                    await Clients.Client(hostConn).TryConnect(username, password, screenShareId);
                }
                catch (Exception ex)
                {
                    DebugWrite($"TryConnect -> host error: {ex.Message}");
                }
            }
        }

        public async Task AuthenticateSuccess(string clientId, string screenShareId)
        {
            if (HostByScreenId.TryGetValue(screenShareId, out var hostConn))
            {
                try
                {
                    await Clients.Client(hostConn).AuthenticateSuccess(clientId, screenShareId);
                }
                catch (Exception ex)
                {
                    DebugWrite($"AuthenticateSuccess -> host error: {ex.Message}");
                }
            }
        }

        // -------------------------
        // CONNECTION LIFECYCLE
        // -------------------------
        public override Task OnReconnected()
        {
            var connectionId = Context.ConnectionId;
            try
            {
                Clients.Client(connectionId).RequestToReSub();
            }
            catch { }
            return base.OnReconnected();
        }

        public override Task OnDisconnected(bool stopCalled)
        {
            var connectionId = Context.ConnectionId;

            // Remove from any groups this connection belonged to
            if (ConnectionGroups.TryRemove(connectionId, out var bag))
            {
                foreach (var group in bag)
                {
                    // best-effort remove
                    Groups.Remove(connectionId, group).Wait();
                }
            }

            // If this connection was a registered host, remove host entry(s)
            var hostsToRemove = HostByScreenId.Where(kv => kv.Value == connectionId).Select(kv => kv.Key).ToList();
            foreach (var screenId in hostsToRemove)
            {
                HostByScreenId.TryRemove(screenId, out _);
                // notify group that host disconnected
                try
                {
                    Clients.Group(screenId).StopScreenShare();
                }
                catch { }
            }

            return base.OnDisconnected(stopCalled);
        }

        // -------------------------
        // HELPERS
        // -------------------------
        private void AddConnectionGroup(string connectionId, string screenShareId)
        {
            var bag = ConnectionGroups.GetOrAdd(connectionId, _ => new ConcurrentBag<string>());
            bag.Add(screenShareId);
        }

        private void RemoveConnectionGroup(string connectionId, string screenShareId)
        {
            if (!ConnectionGroups.TryGetValue(connectionId, out var bag)) return;

            // ConcurrentBag has no Remove; rebuild if necessary.
            var newBag = new ConcurrentBag<string>(bag.Where(x => x != screenShareId));
            ConnectionGroups[connectionId] = newBag;
        }

        private void DebugWrite(string message)
        {
            // Replace with your logger. Keep simple for now.
            try
            {
                System.Diagnostics.Trace.WriteLine($"[SignalR] {message}");
            }
            catch { }
        }
    }
}
