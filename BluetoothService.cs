#if ANDROID
using Android.Bluetooth;
using Android.Content;
using Java.Util;
using Microsoft.Maui.ApplicationModel;
using System.Text;

namespace Bluetooth
{
    public class BluetoothService : IDisposable
    {
        static readonly UUID? ServiceUUID =
            UUID.FromString("00001101-0000-1000-8000-00805F9B34FB");

        BluetoothAdapter? _adapter;
        BluetoothSocket? _clientSocket;
        BluetoothServerSocket? _serverSocket;
        BluetoothSocket? _connectedSocket; // ← keeps accepted socket alive

        public event Action<string>? TextReceived;
        public event Action<byte[]>? ImageReceived;
        public event Action<string>? StatusChanged;

        public BluetoothService()
        {
            var btManager = Platform.CurrentActivity?
                .GetSystemService(Context.BluetoothService) as BluetoothManager;
            _adapter = btManager?.Adapter;
        }

        public bool IsBluetoothEnabled => _adapter?.IsEnabled ?? false;

        // ── RECEIVER SIDE ────────────────────────────────────────────
        public async Task StartListeningAsync(CancellationToken ct)
        {
            if (_adapter == null) return;

            // Close old server socket if any
            try { _serverSocket?.Close(); } catch { }

            _serverSocket = _adapter.ListenUsingRfcommWithServiceRecord(
                "MauiBTShare", ServiceUUID);

            StatusChanged?.Invoke("Waiting for connection...");

            await Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Block here until sender connects
                        BluetoothSocket? socket = null;

                        await Task.Run(() =>
                        {
                            socket = _serverSocket?.Accept(); // blocks until connected
                        }, ct);

                        if (socket != null && socket.IsConnected)
                        {
                            _connectedSocket = socket;

                            MainThread.BeginInvokeOnMainThread(() =>
                                StatusChanged?.Invoke("Device connected! Waiting for data..."));

                            // Handle data then loop back to accept next connection
                            await HandleIncomingAsync(socket, ct);

                            MainThread.BeginInvokeOnMainThread(() =>
                                StatusChanged?.Invoke("Transfer done. Waiting for next connection..."));
                        }
                    }
                    catch (Java.IO.IOException ex)
                    {
                        if (ct.IsCancellationRequested) break;

                        // Server socket closed — restart it
                        MainThread.BeginInvokeOnMainThread(() =>
                            StatusChanged?.Invoke($"Socket error: {ex.Message}. Restarting..."));

                        try
                        {
                            _serverSocket?.Close();
                            _serverSocket = _adapter.ListenUsingRfcommWithServiceRecord(
                                "MauiBTShare", ServiceUUID);
                        }
                        catch { break; }
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }, ct);
        }

        async Task HandleIncomingAsync(BluetoothSocket socket, CancellationToken ct)
        {
            try
            {
                var inputStream = socket.InputStream;
                if (inputStream == null) return;

                // Read type byte
                var typeBuffer = new byte[1];
                await ReadExactAsync(inputStream, typeBuffer, ct);

                // Read 4-byte length
                var lenBuffer = new byte[4];
                await ReadExactAsync(inputStream, lenBuffer, ct);
                int length = BitConverter.ToInt32(lenBuffer, 0);

                if (length <= 0 || length > 10_000_000) // max 10MB
                    throw new Exception($"Invalid data length: {length}");

                // Read full payload
                var data = new byte[length];
                await ReadExactAsync(inputStream, data, ct);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (typeBuffer[0] == 0x01)
                        TextReceived?.Invoke(Encoding.UTF8.GetString(data));
                    else if (typeBuffer[0] == 0x02)
                        ImageReceived?.Invoke(data);
                });
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                    StatusChanged?.Invoke($"Receive error: {ex.Message}"));
            }
            finally
            {
                // Don't close socket here — keep it open for multiple transfers
            }
        }

        static async Task ReadExactAsync(
            System.IO.Stream stream,
            byte[] buffer,
            CancellationToken ct)
        {
            int totalRead = 0;
            int remaining = buffer.Length;

            while (remaining > 0)
            {
                int n = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, remaining), ct);

                if (n == 0)
                    throw new System.IO.EndOfStreamException(
                        "Stream closed before all bytes received.");

                totalRead += n;
                remaining -= n;
            }
        }

        // ── SENDER SIDE ──────────────────────────────────────────────
        public async Task ConnectToDeviceAsync(BluetoothDevice device)
        {
            _clientSocket?.Close();
            _clientSocket = null;

            _adapter?.CancelDiscovery();

            // Small delay after cancelling discovery
            await Task.Delay(300);

            try
            {
                // Method 1 — standard RFCOMM
                _clientSocket = device.CreateRfcommSocketToServiceRecord(ServiceUUID);
                await Task.Run(() => _clientSocket?.Connect());
            }
            catch (Java.IO.IOException)
            {
                // Method 2 — reflection fallback (more compatible with Android 9+)
                try
                {
                    _clientSocket?.Close();

                    var method = device.Class.GetMethod(
                        "createRfcommSocket",
                        Java.Lang.Integer.Type);

                    _clientSocket = method?.Invoke(device, 1) as BluetoothSocket;

                    // Small delay before retry connect
                    await Task.Delay(500);

                    await Task.Run(() => _clientSocket?.Connect());
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"Could not connect: {ex.Message}");
                }
            }
        }

        public async Task SendTextAsync(string text)
        {
            if (_clientSocket == null || !_clientSocket.IsConnected)
                throw new InvalidOperationException("Not connected.");

            var data = Encoding.UTF8.GetBytes(text);
            await SendPayloadAsync(0x01, data);
        }

        public async Task SendImageAsync(byte[] imageBytes)
        {
            if (_clientSocket == null || !_clientSocket.IsConnected)
                throw new InvalidOperationException("Not connected.");

            await SendPayloadAsync(0x02, imageBytes);
        }

        async Task SendPayloadAsync(byte type, byte[] data)
        {
            var outputStream = _clientSocket?.OutputStream
                ?? throw new InvalidOperationException("No output stream.");

            // Protocol: [1 byte type][4 bytes length][N bytes data]
            await outputStream.WriteAsync(new[] { type }, 0, 1);
            await outputStream.WriteAsync(
                BitConverter.GetBytes(data.Length), 0, 4);
            await outputStream.WriteAsync(data, 0, data.Length);
            await outputStream.FlushAsync();
        }

        public void Dispose()
        {
            _clientSocket?.Close();
            _connectedSocket?.Close();
            _serverSocket?.Close();
        }
    }
}
#endif