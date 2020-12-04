using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DBusTest.message;
using DBusTest.utils;
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

            BlueZManager.isBlueZ5().ContinueWith(task =>
            {
                if(!task.Result)
                    Console.WriteLine("WARNING: BlueZ 5 not detected");
            });
            
            var deviceAddress = args[0];

            BluetoothService service = new BluetoothService();
            service.ReadyForConnection += (sender, handle) =>
            {
                Console.WriteLine("Reached external CB.");
            };
            service.Connected += Connected;
            service.MessageReceived += MessageReceived;

            service.SelectAdapter(args[1]).Wait();

            service.Connect(deviceAddress).Wait();
            
            Thread.Sleep(50000);

            /*await device.DisconnectAsync();
            Console.WriteLine("Disconnected.");*/
        }
        
        /***
         * Download coredumps:
         */

        private static void Connected(object? sender, EventArgs e)
        {
            //BluetoothService.Instance.SendAsync(SPPMessage.MessageIds.UNK_SHUTDOWN);

            
            BluetoothService.Instance.SendAsync(SPPMessage.MessageIds.MSG_ID_LOG_SESSION_OPEN);
            BluetoothService.Instance.SendAsync(SPPMessage.MessageIds.MSG_ID_LOG_COREDUMP_DATA_SIZE);

        }

        private static byte[] _buffer;
        
        /* DATA_SIZE */
        private static int _totalSize;
        private static short _maxFragmentSize;
        private static int _fragmentCount => (int)Math.Ceiling((double)_totalSize/_maxFragmentSize);
        
        /* DATA */
        private static int _currentOffset = 0;
        
        private static void MessageReceived(object? sender, SPPMessage e)
        {
            switch (e.Id)
            {
                case SPPMessage.MessageIds.MSG_ID_LOG_COREDUMP_DATA_SIZE:
                    _totalSize = BitConverter.ToInt32(e.Payload);
                    _maxFragmentSize = BitConverter.ToInt16(e.Payload);
                    _buffer = new byte[_totalSize];

                    if (_totalSize <= 0)
                    {
                        Console.WriteLine("No coredump available.");
                    }

                    BluetoothService.Instance.SendAsync(SPPMessage.MessageIds.MSG_ID_LOG_COREDUMP_DATA, 
                        ByteArrayUtils.Combine(BitConverter.GetBytes(_currentOffset), BitConverter.GetBytes(_totalSize)));
                    break;
                
                case SPPMessage.MessageIds.MSG_ID_LOG_COREDUMP_DATA:
                    _totalSize = BitConverter.ToInt32(e.Payload);
                    _maxFragmentSize = BitConverter.ToInt16(e.Payload);
                    _buffer = new byte[_totalSize];

                    if (_totalSize <= 0)
                    {
                        Console.WriteLine("No coredump available.");
                    }

                    BluetoothService.Instance.SendAsync(SPPMessage.MessageIds.MSG_ID_LOG_COREDUMP_DATA, 
                        ByteArrayUtils.Combine(BitConverter.GetBytes(_currentOffset), BitConverter.GetBytes(_totalSize)));
                    break;
            }
        }
    }
}