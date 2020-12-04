using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using DBusTest.message;
using DBusTest.model.Constants;
using DBusTest.utils;
using ThePBone.BlueZNet;
using ThePBone.BlueZNet.Interop;
using Tmds.DBus;

namespace DBusTest
{
    public class BluetoothService
    {
        private static BluetoothService _instance = null;
        private static readonly object SingletonPadlock = new object();

        public static BluetoothService Instance
        {
            get
            {
                lock (SingletonPadlock)
                {
                    return _instance ??= new BluetoothService();
                }
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        public EventHandler Connected;
        public EventHandler ReadyForConnection;
        public EventHandler<string> ConnectionLost;
        public EventHandler<SPPMessage> MessageReceived;

        private static readonly ConcurrentQueue<SPPMessage> _transmitterQueue = new ConcurrentQueue<SPPMessage>();
        
        private CancellationTokenSource _cancelSource = new CancellationTokenSource();
        private Task _btservice;
        private readonly BluetoothSocket _profile = new BluetoothSocket();
        private IAdapter1 _adapter;
        private IDevice1 _device;

        private string _currentMac = String.Empty;
        public Model ActiveModel { get; set; } = Model.BudsPlus;
        
        private IDisposable _connectionWatchdog;
        private IDisposable ConnectionWatchdog
        {
            get => _connectionWatchdog;
            set
            {
                /* Update connection watcher */
                _connectionWatchdog?.Dispose();
                _connectionWatchdog = value;
            }
        }

        private Guid ServiceUuid
        {
            get
            {
                switch (ActiveModel)
                {
                    case Model.Buds:
                        return new Guid("{00001102-0000-1000-8000-00805f9b34fd}");
                    case Model.BudsPlus:
                        return new Guid("{00001101-0000-1000-8000-00805F9B34FB}");
                    case Model.BudsLive:
                        return new Guid("{00001101-0000-1000-8000-00805F9B34FB}");
                    default:
                        return new Guid("{00001101-0000-1000-8000-00805F9B34FB}");
                }
            }
        }

        public BluetoothService()
        {
            ReadyForConnection += OnReadyForConnection;
        }

        private async void OnReadyForConnection(object? sender, EventArgs e)
        {
            Console.WriteLine("Attempting to auto-connect to profile...");
            await Connect(_currentMac);
        }


        private void BluetoothServiceLoop()
        {
            while (true)
            {
                _cancelSource.Token.ThrowIfCancellationRequested();

                /* Handle incoming stream */
                byte[] buffer = new byte[2048];
                bool dataAvailable = false;
                try
                {
                    dataAvailable = _profile.Stream.Read(buffer, 0, buffer.Length) >= 0;
                }
                catch (UnixSocketException ex)
                {
                    Console.WriteLine(
                        "BluetoothServiceLoop: SocketException thrown. Immediately shutting service down.");
                    ConnectionLost?.Invoke(this, ex.Message);
                    throw;
                }

                if (dataAvailable)
                {
                    ArrayList data = new(buffer);
                    do
                    {
                        try
                        {
                            SPPMessage msg = SPPMessage.DecodeMessage(data.OfType<byte>().ToArray());

                            Console.WriteLine($"<< Incoming: {msg}");
                            MessageReceived?.Invoke(this, msg);

                            if (msg.TotalPacketSize >= data.Count)
                                break;

                            data.RemoveRange(0, msg.TotalPacketSize);

                            /* BlueZ/Linux only */
                            if (data.Count > 0 && data.OfType<byte>().ElementAt(0) == 0)
                            {
                                /* If the next byte is NULL we can expect that the frame contains no more data */
                                data.Clear();
                            }
                        }
                        catch (InvalidDataException e)
                        {
                            data.Clear();
                            Console.WriteLine("Invalid data received. Skipping frame.");
                        }
                    } while (data.Count > 0);
                }

                /* Handle outgoing stream */
                lock (_transmitterQueue)
                {
                    if (_transmitterQueue.Count > 0)
                    {
                        if (_transmitterQueue.TryDequeue(out var msg))
                        {
                            byte[] raw = msg.EncodeMessage();
                            try
                            {
                                Console.WriteLine($">> Outgoing: {msg}");
                                _profile.Stream.Write(raw, 0, raw.Length);
                            }
                            catch (SocketException e)
                            {
                                ConnectionLost(this, e.Message);
                            }
                            catch (IOException e)
                            {
                                if (e.InnerException != null && e.InnerException.GetType() == typeof(SocketException))
                                {
                                    ConnectionLost(this, e.Message);
                                }
                            }

                            Task.Delay(1000).Wait();
                        }
                    }
                }

            }
        }

        public void SendAsync(SPPMessage msg)
            {
                lock (_transmitterQueue)
                {
                    _transmitterQueue.Enqueue(msg);
                }        
            }
        
            public void SendAsync(SPPMessage.MessageIds id, byte[]? payload = null)
            {
                lock (_transmitterQueue)
                {
                    _transmitterQueue.Enqueue(new SPPMessage {Id = id, Type = SPPMessage.MsgType.Request, Payload = payload ?? Array.Empty<byte>()});
                }
            }
        
            public async Task SelectAdapter(string preferred = "")
            {
                if (preferred.Length > 0)
                {
                    try
                    {
                        _adapter = await BlueZManager.GetAdapterAsync(preferred);
                    }
                    catch(BlueZException ex)
                    {
                        Console.WriteLine($"Preferred adapter not available: " + ex.ErrorName);
                        _adapter = null;
                    }
                }
            
                if(_adapter == null || preferred.Length == 0)
                {
                    var adapters = await BlueZManager.GetAdaptersAsync();
                    if (adapters.Count == 0)
                    {
                        throw new BlueZException(BlueZException.ErrorCodes.NoAdaptersAvailable, "No Bluetooth adapters found.");
                    }

                    _adapter = adapters.First();
                }
            
                var adapterPath = _adapter.ObjectPath.ToString();
                var adapterName = adapterPath.Substring(adapterPath.LastIndexOf("/", StringComparison.Ordinal) + 1);
                Console.WriteLine($"Using Bluetooth adapter {adapterName}");
            }

            public async Task Disconnect()
            {

                if (_btservice == null || _btservice.Status == TaskStatus.Created)
                {
                    Console.WriteLine($"BluetoothServiceLoop not yet launched. No need to cancel.");
                }
                else
                {
                    Console.WriteLine($"Cancelling BluetoothServiceLoop...");
                    _cancelSource.Cancel();
                }

                /* Disconnect device if not already done... */
                if (_device != null)
                {
                    try
                    {
                        await _device.DisconnectProfileAsync(ServiceUuid.ToString());
                        Console.WriteLine($"Profile disconnected.");
                    }
                    catch (DBusException)
                    {
                        /* Discard non-critical exceptions. */
                    }
                }

                /* Attempt to unregister profile if not already done... */
                var profileManager = Connection.System.CreateProxy<IProfileManager1>(BluezConstants.DbusService, "/org/bluez");
                try
                {
                    await profileManager.UnregisterProfileAsync(_profile.ObjectPath);
                    Console.WriteLine($"Profile unregistered.");
                }
                catch (DBusException)
                {
                    /* Discard non-critical exceptions. */
                }
            
            }
        
            public async Task Connect(string macAddress)
            {
                if (_adapter == null)
                {
                    Console.WriteLine("Warning: No adapter preselected");
                    await SelectAdapter();
                }
            
                _device = await _adapter.GetDeviceAsync(macAddress);
                if (_device == null)
                {
                    Console.WriteLine(
                        $"Bluetooth peripheral with address '{macAddress}' not found. Use `bluetoothctl` or Bluetooth Manager to scan and possibly pair first.");
                    return;
                }

                _currentMac = macAddress;
            
                var conn = new Connection(Address.System);
                await conn.ConnectAsync();
            
                _profile.NewConnection = (path, handle, arg3) => ConnectionEstablished();
                _profile.RequestDisconnection = async (path, handle) => await Disconnect();
                await conn.RegisterObjectAsync(_profile);

                int connectionAttempt = 0;
                RetryConnect:
                Console.WriteLine("Connecting...");

                try
                {
                    await _device.ConnectAsync();
                }
                catch (DBusException e)
                {
                    var ex = new BlueZException(e);

                    switch (ex.ErrorCode)
                    {
                        case BlueZException.ErrorCodes.InProgress when connectionAttempt >= 10:
                            Console.WriteLine("Gave up after 10 attempts.");
                            throw new TimeoutException("BlueZ timed out");
                        case BlueZException.ErrorCodes.InProgress:
                            Console.WriteLine("Already connecting. Retrying...");
                            await Task.Delay(100);
                            connectionAttempt++;
                            goto RetryConnect;
                        
                        case BlueZException.ErrorCodes.AlreadyConnected:
                            Console.WriteLine("Already connected.");
                            break;
                        default:
                            /* org.bluez.Error.NotReady, org.bluez.Error.Failed */
                            Console.WriteLine("Warning: Connect call failed due to " + ex.ErrorMessage);
                            throw ex;
                    }
                }

                await _device.WaitForPropertyValueAsync("Connected", value: true, Timeout);
                ConnectionWatchdog = _device.WatchForPropertyChangeAsync("Connected", true,state =>
                {
                    if (state)
                    {
                        ReadyForConnection?.Invoke(this, null);
                    }
                    else
                    {
                        ConnectionLost?.Invoke(this, "Reported as disconnected by Bluez");
                    }
                });
                Console.WriteLine("Connected.");
            
                var uuid = ServiceUuid.ToString();
                var properties = new Dictionary<string, object>();
                properties["Role"] = "client";
                properties["Service"] = uuid;
                properties["Name"] = "btsocketnet";
            
                var profileManager = conn.CreateProxy<IProfileManager1>(BluezConstants.DbusService, "/org/bluez");

                try
                {
                    await profileManager.RegisterProfileAsync(_profile.ObjectPath, uuid, properties);
                }
                catch (DBusException e)
                {
                    var ex = new BlueZException(e);

                    switch (ex.ErrorCode)
                    {
                        case BlueZException.ErrorCodes.AlreadyExists:
                            Console.WriteLine("Already registered.");
                            break;
                        case BlueZException.ErrorCodes.InvalidArguments:
                            Console.WriteLine("Invalid arguments. Cannot continue.");
                            throw ex;
                        default:
                            /* Other unknown dbus errors */
                            throw;
                    }
                }
            
                Console.WriteLine("Registered profile with BlueZ");

                Console.WriteLine("Connecting to profile...");
                try
                {
                    await _device.ConnectProfileAsync(uuid);
                }
                catch (DBusException e)
                {
                    var ex = new BlueZException(e);

                    switch (ex.ErrorCode)
                    {
                        case BlueZException.ErrorCodes.AlreadyConnected:
                            Console.WriteLine("Already connected.");
                            break;
                        case BlueZException.ErrorCodes.ConnectFailed:
                            throw ex;
                        case BlueZException.ErrorCodes.DoesNotExist:
                            Console.WriteLine("Profile does not exist. Unsupported device.");
                            throw new PlatformNotSupportedException("Device does not support SPP profile", ex);
                        default:
                            /* Other unknown dbus errors */
                            throw;
                    }
                }
            }

            private void ConnectionEstablished()
            {
                Console.WriteLine("Reached internal CB.");
                Console.WriteLine("Launching BluetoothServiceLoop...");

                _btservice?.Dispose();
                _cancelSource = new CancellationTokenSource();
                _btservice = Task.Run(BluetoothServiceLoop, _cancelSource.Token);
                Connected?.Invoke(this, null);
            }
        }
    }