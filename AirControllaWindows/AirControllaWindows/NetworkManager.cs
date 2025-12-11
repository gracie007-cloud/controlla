

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Newtonsoft.Json.Linq;

namespace AirControllaWindows
{
    public class FirewallException : Exception
    {
        public FirewallException(string message) : base(message) { }
    }


    /// <summary>
    /// Handles network discovery (Bonjour/mDNS) and HTTP server for receiving commands from iPhone
    /// Mirrors the Swift NetworkManager from macOS version
    /// </summary>
    public class NetworkManager
    {
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _listenerCancellation;
        private readonly Dictionary<int, TcpClient> _connections = new Dictionary<int, TcpClient>();
        private int _nextConnectionId = 0;
        private ServiceDiscovery? _mdnsService;
        private ServiceProfile? _serviceProfile;
        private int _port = 0; // 0 = auto-assign by OS
        private const string ServiceType = "_controlla._tcp";

        public int Port => _port;

        public bool IsReceiving { get; private set; }
        public string ReceiverStatus { get; private set; } = "Not Started";
        public bool IsControllerConnected { get; private set; }

        public event EventHandler<string>? StatusChanged;

        /// <summary>
        /// Start the receiver: HTTP server + Bonjour advertising
        /// </summary>
        public async Task StartReceiver()
        {
            if (IsReceiving) return;

            try
            {
                UpdateStatus("Starting receiver...");

                // Start TCP listener
                bool tcpStarted = await StartTcpListener();
                if (!tcpStarted)
                {
                    UpdateStatus("Failed to start TCP listener.");
                    return;
                }

                // Advertise service via Bonjour/mDNS
                bool mdnsStarted = await AdvertiseService();
                if (!mdnsStarted)
                {
                    UpdateStatus("Failed to start mDNS advertising.");
                    return;
                }

                IsReceiving = true;
                UpdateStatus($"Listening on port {Port}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
                Console.WriteLine($"❌ Failed to start receiver: {ex}");
            }
        }

        /// <summary>
        /// Stop receiver and clean up
        /// </summary>
        public async Task StopAll()
        {
            if (!IsReceiving) return;

            try
            {
                // Stop TCP listener
                _listenerCancellation?.Cancel();
                _tcpListener?.Stop();
                _tcpListener = null;

                foreach (var client in _connections.Values)
                {
                    client?.Close();
                }
                _connections.Clear();

                // Stop mDNS advertising
                if (_serviceProfile != null && _mdnsService != null)
                {
                    _mdnsService.Unadvertise(_serviceProfile);
                }
                _mdnsService?.Dispose();
                _mdnsService = null;
                _serviceProfile = null;

                IsReceiving = false;
                IsControllerConnected = false;
                UpdateStatus("Stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error stopping receiver: {ex}");
            }
        }

        /// <summary>
        /// Start TCP listener to receive commands from iPhone
        /// Mirrors the Swift NWListener implementation
        /// </summary>
        private async Task<bool> StartTcpListener()
        {
            try
            {
                // Find available port starting from 8080
                _port = FindAvailablePort(8080);

                _tcpListener = new TcpListener(IPAddress.Any, _port);
                _tcpListener.Start();
                _listenerCancellation = new CancellationTokenSource();

                Console.WriteLine($"✅ TCP server started on port {_port}");
                UpdateStatus($"Ready - {Dns.GetHostName()}");

                // Start accepting connections in background
                _ = Task.Run(() => AcceptClientsAsync(_listenerCancellation.Token));
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            {
                return false;
            }
        }

        private int FindAvailablePort(int startPort)
        {
            for (int port = startPort; port < startPort + 100; port++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch
                {
                    continue;
                }
            }
            return startPort;
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            if (_tcpListener == null) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _tcpListener!.AcceptTcpClientAsync();
                    var connectionId = _nextConnectionId++;
                    _connections[connectionId] = client;

                    Console.WriteLine($"✅ Client connected: {connectionId}");

                    // Handle this connection in background
                    _ = Task.Run(() => HandleClientAsync(client, connectionId, cancellationToken));
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, int connectionId, CancellationToken cancellationToken)
        {
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 8192;
                client.ReceiveBufferSize = 8192;

                var stream = client.GetStream();
                var buffer = new byte[8192];
                var requestDataBuilder = new StringBuilder();

                if (!IsControllerConnected)
                {
                    IsControllerConnected = true;
                    UpdateStatus("Controller connected");
                }

                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) break;

                    requestDataBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    
                    // Process all complete requests in the buffer
                    while (ProcessBufferedRequests(requestDataBuilder, stream)) { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Connection error: {ex.Message}");
            }
            finally
            {
                _connections.Remove(connectionId);
                client.Close();

                if (_connections.Count == 0)
                {
                    IsControllerConnected = false;
                    UpdateStatus("Waiting for connection");
                }
            }
        }

        private bool ProcessBufferedRequests(StringBuilder requestDataBuilder, NetworkStream stream)
        {
            string currentRequestData = requestDataBuilder.ToString();
            var headerEndIndex = currentRequestData.IndexOf("\r\n\r\n");
            if (headerEndIndex == -1)
            {
                return false; // Not enough data for headers yet
            }

            var headersPart = currentRequestData.Substring(0, headerEndIndex);
            var headers = headersPart.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var contentLengthHeader = headers.FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            
            int contentLength = 0;
            if (contentLengthHeader != null)
            {
                var lengthStr = contentLengthHeader.Split(':')[1].Trim();
                int.TryParse(lengthStr, out contentLength);
            }

            var bodyStartIndex = headerEndIndex + 4;
            if (currentRequestData.Length < bodyStartIndex + contentLength)
            {
                return false; // Not enough data for the full body yet
            }

            var requestString = currentRequestData.Substring(0, bodyStartIndex + contentLength);
            var jsonBody = currentRequestData.Substring(bodyStartIndex, contentLength);

            // Process this single, complete request
            // Use Task.Run to avoid blocking the network loop for long, but await a sub-task to ensure response is sent.
            Task.Run(async () =>
            {
                await ProcessHttpRequestAsync(requestString, jsonBody, stream);
            });

            // Remove the processed request from the builder
            requestDataBuilder.Remove(0, bodyStartIndex + contentLength);

            return true; // A request was processed, check for more
        }

        private async Task ProcessHttpRequestAsync(string request, string jsonBody, NetworkStream stream)
        {
            try
            {
                // JSON body is now passed in directly, no need to extract it from the request.
                if (string.IsNullOrWhiteSpace(jsonBody))
                {
                    Console.WriteLine("⚠️ Received request with empty body.");
                    return;
                }

                // Clean up jsonBody - remove any trailing non-JSON characters (like next HTTP request start)
                jsonBody = jsonBody.Trim();

                JObject json;
                try
                {
                    json = JObject.Parse(jsonBody);
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    // If parsing fails, it might have extra data - try to extract just the JSON object
                    int jsonEnd = jsonBody.IndexOf('}');
                    if (jsonEnd > 0)
                    {
                        jsonBody = jsonBody.Substring(0, jsonEnd + 1);
                        json = JObject.Parse(jsonBody);
                    }
                    else
                    {
                        throw;
                    }
                }

                // Determine endpoint from request line
                if (request.Contains("POST /keyboard/text"))
                {
                    HandleKeyboardText(json);
                }
                else if (request.Contains("POST /keyboard/key"))
                {
                    HandleKeyboardKey(json);
                }
                else if (request.Contains("POST /mouse/move"))
                {
                    HandleMouseMove(json);
                }
                else if (request.Contains("POST /mouse/click"))
                {
                    HandleMouseClick(json);
                }

                // Send HTTP response asynchronously to avoid blocking
                var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"success\":true}";
                var responseBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
            }
            catch (Newtonsoft.Json.JsonReaderException jsonEx)
            {
                Console.WriteLine($"❌ Error parsing JSON body: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing request: {ex.Message}");
            }
        }

        private void HandleKeyboardText(JObject json)
        {
            string text = json["text"]?.ToString() ?? "";
            Console.WriteLine($"📨 Keyboard text: {text.Substring(0, Math.Min(text.Length, 50))}...");
            // Offload to ThreadPool to avoid blocking network thread
            Task.Run(() => InputSimulator.TypeText(text));
        }

        private void HandleKeyboardKey(JObject json)
        {
            byte keycode = json["keycode"]?.Value<byte>() ?? 0;
            byte modifier = json["modifier"]?.Value<byte>() ?? 0;
            Console.WriteLine($"📨 Key press: keycode={keycode}, modifier={modifier}");
            // Offload to ThreadPool to avoid blocking network thread
            Task.Run(() => InputSimulator.SendKeyPress(keycode, modifier));
        }

        private void HandleMouseMove(JObject json)
        {
            int deltaX = json["deltaX"]?.Value<int>() ?? 0;
            int deltaY = json["deltaY"]?.Value<int>() ?? 0;
            // Offload to ThreadPool to avoid blocking network thread
            Task.Run(() => InputSimulator.MoveMouse(deltaX, deltaY));
        }

        private void HandleMouseClick(JObject json)
        {
            string button = json["button"]?.ToString() ?? "left";
            Console.WriteLine($"📨 Mouse click: {button}");
            // Offload to ThreadPool to avoid blocking network thread
            Task.Run(() => InputSimulator.ClickMouse(button));
        }

        /// <summary>
        /// Advertise service via Bonjour/mDNS so iPhone can discover this PC
        /// Mirrors the Swift NWListener.advertise() implementation
        /// </summary>
        private async Task<bool> AdvertiseService()
        {
            try
            {
                string hostname = Dns.GetHostName();
                string serviceName = hostname;

                // Create mDNS service discovery
                _mdnsService = new ServiceDiscovery();

                // Create service profile with full details
                _serviceProfile = new ServiceProfile(serviceName, ServiceType, (ushort)_port);

                // Add resources to help with discovery
                var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Where(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && !System.Net.IPAddress.IsLoopback(addr.Address))
                    .Select(addr => addr.Address)
                    .ToList();

                foreach (var addr in addresses)
                {
                    _serviceProfile.Resources.Add(new Makaretu.Dns.ARecord
                    {
                        Name = _serviceProfile.HostName,
                        Address = addr
                    });
                    Console.WriteLine($"   IP: {addr}");
                }

                // Advertise the service
                _mdnsService.Advertise(_serviceProfile);

                Console.WriteLine($"✅ Advertising via mDNS as: {serviceName}");
                Console.WriteLine($"   Service type: {ServiceType}");
                Console.WriteLine($"   Port: {_port}");
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            {
                return false;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: mDNS advertising failed");
                Console.WriteLine($"❌ Failed to advertise service: {ex}");
                return false;
            }
        }

        private void UpdateStatus(string status)
        {
            ReceiverStatus = status;
            StatusChanged?.Invoke(this, status);
        }
    }
}
