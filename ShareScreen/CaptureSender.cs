using CommunicationLibrary.Communication;
using CommunicationLibrary.Models;
using HelpersLibrary.Helpers;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace ShareScreen
{
    public class CaptureSender
    {
        private bool _running = false;
        private readonly int _fps;
        private readonly string _connectedClient;
        private Task _captureTask;

        public CaptureSender(string connectedClient, int fps = 10)
        {
            _connectedClient = connectedClient;
            _fps = fps;
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _captureTask = Task.Run(() => CaptureLoop());
        }

        public void Stop()
        {
            _running = false;
        }

        private void CaptureLoop()
        {
            int delay = 1000 / _fps;

            while (_running)
            {
                try
                {
                    // captura a imagem com compressão de 50%
                    Size originalResolution;
                    byte[] screenshot = ImageHelper.TakeScreenshot(
                        out originalResolution,
                        width: 0,     // resolução original
                        height: 0,
                        compressionLevel: 50
                    );

                    // envia para o hub (SignalR)
                    Communicator.Instance.ProduceScreenshot(
                        screenshot,
                        originalResolution.Width,
                        originalResolution.Height,
                        _connectedClient
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Erro na captura: " + ex.Message);
                }

                Thread.Sleep(delay);
            }
        }

        public void SendMouseMove(int x, int y)
        {
            Communicator.Instance.ProduceMouseMove(x, y, _connectedClient);
        }
    }
}
