using CommunicationLibrary.Models;
using HelpersLibrary.Helpers;
using Microsoft.AspNet.SignalR.Client;
using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationLibrary.Communication
{
    public sealed class Communicator
    {
        private static readonly Communicator instance = new Communicator();
        public delegate void SignalRReconnecting(bool connected);
        public event SignalRReconnecting Reconnecting;
        public event SignalRReconnecting ConnectionLost;

        HubConnection _hub;
        IHubProxy _proxy;

        // ---- sending queue state (latest-frame per screenShareId) ----
        class PendingFrame
        {
            public byte[] Data;
            public int Width;
            public int Height;
            public DateTime Timestamp;
        }

        // Mantemos apenas o último frame por screenShareId (drop frames antigos)
        private ConcurrentDictionary<string, PendingFrame> _latestFrames = new ConcurrentDictionary<string, PendingFrame>();

        // Cancellation / worker
        private CancellationTokenSource _cts;
        private Task _senderTask;
        private readonly int _targetFps = 30; // padrão; pode ser alterado

        // Explicit static constructor to tell C# compiler
        // not to mark type as beforefieldinit
        static Communicator()
        {
        }

        private Communicator()
        {
            string ip = ConfigurationManager.AppSettings["BroadcasterIP"];
            string port = ConfigurationManager.AppSettings["BroadcasterPort"];
            _hub = new HubConnection($"http://{ip}:{port}/livehub");
            _hub.Closed += ConnectionClosed;
            _hub.Reconnecting += Hub_Reconnecting;

            _proxy = _hub.CreateHubProxy("livehub");

            TryToConnect();

            // start sender worker
            _cts = new CancellationTokenSource();
            _senderTask = Task.Run(() => SenderWorker(_cts.Token));
        }

        private void Hub_Reconnecting()
        {
            Reconnecting?.Invoke(true);
        }

        private void ConnectionClosed()
        {
            ConnectionLost?.Invoke(false);
        }

        private void TryToConnect()
        {
        RetryConnection:
            try
            {

                _hub.Start().Wait();
                if (_hub.State != ConnectionState.Connected)
                {
                    Thread.Sleep(4000);
                    goto RetryConnection;
                }
            }
            catch (ThreadAbortException exp)
            {
                Thread.ResetAbort();
                Console.WriteLine("Trying to Reconnect ... ");
                goto RetryConnection;
            }
            catch (Exception ex)
            {
                Thread.Sleep(4000);
                Console.WriteLine("Trying to Reconnect ... ");
                goto RetryConnection;
            }
        }

        public static Communicator Instance
        {
            get
            {
                return instance;
            }
        }

        #region Existing API (kept for compatibility)

        public void RegisterClient(string hostId)
        {
            if (_hub.State != ConnectionState.Connected)
                TryToConnect();

            // legacy call kept - forward
            _proxy.Invoke("RegisterClient", _hub.ConnectionId, hostId);
        }

        public void TryConnect(string id, string password, string hostId)
        {
            if (_hub.State != ConnectionState.Connected)
                TryToConnect();

            _proxy.Invoke("TryConnect", id, password, hostId);
        }

        public void ReadyToConnect(Action<bool> clientConnected, Action<string, string, string> tryConnect, Action<string> authenticateSuccess, Action<InputDataComm> produced, Action stopScreenShare, Action requestToResub)
        {
            _proxy.On("ClientConnected", clientConnected);
            _proxy.On("TryConnect", tryConnect);
            _proxy.On("AuthenticateSuccess", authenticateSuccess);
            _proxy.On("Produce", produced);
            _proxy.On("StopScreenShare", stopScreenShare);
            _proxy.On("RequestToReSub", requestToResub);
        }

        public void AuthenticateSuccess(string clientId, string hostId)
        {
            _proxy.Invoke("AuthenticateSuccess", clientId, hostId);
        }

        public void Produce(InputDataComm broadcastDataComm, string connectedClient)
        {
            _proxy.Invoke("Produce", broadcastDataComm, connectedClient);
        }

        public void ProduceMouseMove(int x, int y, string connectedClient)
        {
            _proxy.Invoke("ProduceMouseMove", x, y, connectedClient);
        }

        public void StopScreenShare(string hostId)
        {
            _proxy.Invoke("StopScreenShare", hostId);
        }

        public void ReadyToReceiveInput(Action<int, int> mouseMoved, Action<byte[], string, string> screenshotReceived)
        {
            _proxy.On("ProduceMouseMove", mouseMoved);
            _proxy.On("ProduceScreenshot", screenshotReceived);
        }

        #endregion

        // -------------------------
        // NEW: enqueue / async sending API
        // -------------------------
        /// <summary>
        /// Enqueue a frame to be sent for screenShareId. The worker will send at most _targetFps and drop older frames.
        /// </summary>
        public void ProduceScreenshot(byte[] image, int width, int height, string screenShareId)
        {
            if (image == null || string.IsNullOrEmpty(screenShareId)) return;

            var pf = new PendingFrame()
            {
                Data = image,
                Width = width,
                Height = height,
                Timestamp = DateTime.UtcNow
            };
            _latestFrames.AddOrUpdate(screenShareId, pf, (k, old) => pf);
        }

        /// <summary>
        /// Direct immediate send (sync) - kept for compatibility but not recommended for high FPS.
        /// </summary>
        public async Task ProduceScreenshotImmediateAsync(byte[] image, int width, int height, string screenShareId)
        {
            if (image == null || string.IsNullOrEmpty(screenShareId)) return;
            try
            {
                await _proxy.Invoke("ProduceScreenshot", image, width.ToString(), height.ToString(), screenShareId);
            }
            catch (Exception ex)
            {
                TraceWrite($"ProduceScreenshotImmediate error: {ex.Message}");
            }
        }

        // background worker that runs at target fps and sends the latest frame of each screenShareId
        private async Task SenderWorker(CancellationToken ct)
        {
            var delay = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    // snapshot keys to iterate
                    var keys = _latestFrames.Keys;
                    foreach (var k in keys)
                    {
                        if (ct.IsCancellationRequested) break;

                        if (_latestFrames.TryRemove(k, out var frame))
                        {
                            // attempt send
                            try
                            {
                                // Invoke asynchronously (dont wait long)
                                await _proxy.Invoke("ProduceScreenshot", frame.Data, frame.Width.ToString(), frame.Height.ToString(), k);
                            }
                            catch (Exception ex)
                            {
                                TraceWrite($"Error sending frame for {k}: {ex.Message}");
                                // if send failed, we can drop that frame or re-enqueue (drop for now)
                            }
                        }
                    }

                    sw.Stop();
                    var remain = delay - sw.Elapsed;
                    if (remain > TimeSpan.Zero)
                        await Task.Delay(remain, ct);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    TraceWrite($"SenderWorker error: {ex.Message}");
                    await Task.Delay(100, ct); // small backoff
                }
            }
        }

        public void Disconnect(string hostId)
        {
            try
            {
                if (_hub.State == ConnectionState.Connected)
                    _hub.Stop();
            }
            catch { }
        }

        // Call this to gracefully stop worker when application exiting
        public void Shutdown()
        {
            try
            {
                _cts?.Cancel();
                _senderTask?.Wait(1000);
            }
            catch { }
            finally
            {
                _hub?.Stop();
            }
        }

        private void TraceWrite(string message)
        {
            try
            {
                System.Diagnostics.Trace.WriteLine($"[Communicator] {message}");
            }
            catch { }
        }
    }
}
