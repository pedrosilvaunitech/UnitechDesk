using Microsoft.Owin.Hosting;
using System;
using System.ServiceProcess;

namespace Broadcaster
{
    public partial class Broadcaster : ServiceBase
    {
        public string SignalRAddress = "http://*:7717/";
        private IDisposable _serverSignalR = null;

        public Broadcaster()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                // Inicializa o SignalR
                _serverSignalR = WebApp.Start<StartUpSignalR>(url: SignalRAddress);
                Console.WriteLine("SignalR server started at " + SignalRAddress);

                // Aqui você poderá iniciar a captura de tela depois
                // Ex: CaptureSender.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error starting service: " + ex.Message);
            }
        }

        protected override void OnStop()
        {
            try
            {
                _serverSignalR?.Dispose();
                Console.WriteLine("Service stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error stopping service: " + ex.Message);
            }
        }

        internal void Start()
        {
            OnStart(null);
        }
    }
}
