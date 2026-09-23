using System;
using System.Net;
using WebSocketSharp.Server;

#nullable disable

namespace NurMarketKassa.Services.Hardware
{
    public class ScalePrinterAgentService : IDisposable
    {
        private readonly WebSocketServer _server;

        public ScalePrinterAgentService(string url = "ws://127.0.0.1:8080")
        {
            _server = new WebSocketServer(url);
            _server.AddWebSocketService<DeviceBehavior>("/Device");
        }

        public void Start()
        {
            try
            {
                _server.Start();
                PosLogger.Log($"ScalePrinterAgent запущен: {_server.Address}:{_server.Port}/Device", "HARDWARE");
            }
            catch (Exception ex)
            {
                PosLogger.Log("Не удалось запустить ScalePrinterAgent: " + ex.Message, "ERROR");
            }
        }

        public void Stop()
        {
            _server.Stop();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}