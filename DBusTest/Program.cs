using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThePBone.BlueZNet;
using Tmds.DBus;

namespace DBusTest
{
    class Program
    {

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: <deviceAddress> [adapterName]");
                Console.WriteLine("Example: AA:BB:CC:11:22:33 hci0");
                return;
            }

            var deviceAddress = args[0];

            BluetoothService service = new BluetoothService();
            service.ReadyForConnection += (sender, handle) =>
            {
                Console.WriteLine("Reached external CB.");
            };

            service.SelectAdapter(args[1]).Wait();

            service.Connect(deviceAddress).Wait();

            Thread.Sleep(50000);

            /*await device.DisconnectAsync();
            Console.WriteLine("Disconnected.");*/
        }
    }
}