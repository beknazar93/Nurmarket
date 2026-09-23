using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NurMarketKassa.Configuration;
using WebSocketSharp;
using WebSocketSharp.Server;

#nullable disable

namespace NurMarketKassa.Services.Hardware
{
    public class DeviceBehavior : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.Data == "GET_WEIGHT")
            {
                string weight = ReadWeight();
                Send(weight);
                return;
            }
            if (e.Data.StartsWith("PRINT:"))
            {
                string receiptText = e.Data.Replace("PRINT:", "");
                PrintReceipt(receiptText);
                Send("PRINTED");
                return;
            }
            Send("UNKNOWN_COMMAND");
        }

        private string ReadWeight()
        {
            string result;
            try
            {
                using (ScaleReaderService scaleService = new ScaleReaderService(UserPreferences.Instance.ToScaleSettings()))
                {
                    scaleService.Start();
                    int waited = 0;
                    while (scaleService.LastWeight == null && waited < 2000)
                    {
                        Thread.Sleep(100);
                        waited += 100;
                    }
                    scaleService.Stop();
                    if (scaleService.LastWeight != null)
                    {
                        result = scaleService.LastWeight.Value.ToString("F3");
                    }
                    else
                    {
                        result = "ERROR: нет данных от весов";
                    }
                }
            }
            catch (Exception ex)
            {
                result = "ERROR: " + ex.Message;
            }
            return result;
        }

        private void PrintReceipt(string text)
        {
            try
            {
                var settings = UserPreferences.Instance.ToReceiptPrinterSettings();
                if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.DevicePath))
                {
                    Send("ERROR: принтер не настроен");
                    return;
                }

                ReceiptPrintService.PrintText(settings, text);
            }
            catch (Exception ex)
            {
                Send("ERROR: " + ex.Message);
            }
        }
    }
}