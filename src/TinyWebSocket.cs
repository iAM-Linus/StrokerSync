using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// Minimal WebSocket client using System.Net.Sockets.
/// Uses DataAvailable polling (not ReadTimeout) for reliable ping/pong on Mono/Unity.
/// </summary>
public class TinyWebSocket
{
    private TcpClient _client;
    private NetworkStream _stream;
    private volatile bool _isConnected = false;
    private string _url;
    private Thread _receiveThread;
    private readonly object _sendLock = new object();

    // Fragmented message reassembly (RFC 6455 §5.4)
    private List<byte> _fragmentBuffer;
    private int _fragmentOpcode;

    public bool IsOpen => _isConnected && _client != null && _client.Connected;
    public Action<string> OnMessage;
    public Action<string> OnError;
    public Action OnClose;

    public TinyWebSocket(string url)
    {
        _url = url.Replace("ws://", "").Replace("wss://", "");
    }

    public void Connect()
    {
        try
        {
            string[] parts = _url.Split(':');
            string ip = parts[0];
            int port = parts.Length > 1 ? int.Parse(parts[1]) : 80;

            _client = new TcpClient();
            _client.NoDelay = true;
            _client.SendTimeout = 5000;
            _client.Connect(ip, port);
            _stream = _client.GetStream();

            if (DoHandshake(ip, port))
            {
                _isConnected = true;
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();
            }
            else
            {
                throw new Exception("WebSocket Handshake rejected by server.");
            }
        }
        catch (Exception ex)
        {
            Close();
            throw ex;
        }
    }

    private bool DoHandshake(string ip, int port)
    {
        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 24);

        StringBuilder sb = new StringBuilder();
        sb.Append("GET / HTTP/1.1\r\n");
        sb.Append("Host: " + ip + ":" + port + "\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Key: " + key + "\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        sb.Append("\r\n");

        byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        _stream.Write(headerBytes, 0, headerBytes.Length);

        // Handshake response — one-time blocking read is fine
        _stream.ReadTimeout = 5000;
        byte[] buffer = new byte[2048];
        int bytesRead = _stream.Read(buffer, 0, buffer.Length);
        _stream.ReadTimeout = Timeout.Infinite;
        string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        return response.Contains("HTTP/1.1 101");
    }

    public void Send(string message)
    {
        if (!IsOpen) return;
        SendFrame(0x81, Encoding.UTF8.GetBytes(message));
    }

    private void SendPong(byte[] payload)
    {
        if (!IsOpen) return;
        SendFrame(0x8A, payload);
    }

    private void SendFrame(byte firstByte, byte[] payload)
    {
        try
        {
            List<byte> frame = new List<byte>();
            frame.Add(firstByte);

            byte[] maskKey = new byte[4];
            new System.Random().NextBytes(maskKey);

            if (payload.Length < 126)
            {
                frame.Add((byte)(0x80 | payload.Length));
            }
            else if (payload.Length <= 65535)
            {
                frame.Add((byte)(0x80 | 126));
                frame.Add((byte)((payload.Length >> 8) & 0xFF));
                frame.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                frame.Add((byte)(0x80 | 127));
                for (int i = 7; i >= 0; i--)
                    frame.Add((byte)((payload.Length >> (8 * i)) & 0xFF));
            }

            frame.AddRange(maskKey);

            for (int i = 0; i < payload.Length; i++)
                frame.Add((byte)(payload[i] ^ maskKey[i % 4]));

            byte[] buffer = frame.ToArray();
            lock (_sendLock)
            {
                if (_stream != null && _isConnected)
                    _stream.Write(buffer, 0, buffer.Length);
            }
        }
        catch (Exception)
        {
            Close();
        }
    }

    /// <summary>
    /// Receive loop using DataAvailable polling.
    /// Why not ReadTimeout? On Mono/Unity, ReadTimeout is unreliable:
    /// it can throw SocketException (not IOException), corrupt stream state,
    /// and cause subsequent reads to miss data. DataAvailable is reliable:
    /// when true, Read returns immediately with data. When false, we sleep
    /// 1ms and check again. This ensures we never miss ping frames.
    /// </summary>
    private void ReceiveLoop()
    {
        byte[] headerBuffer = new byte[2];

        while (_isConnected)
        {
            try
            {
                if (_client == null || !_client.Connected)
                    break;

                if (!_stream.DataAvailable)
                {
                    Thread.Sleep(1);
                    continue;
                }

                ReadExact(headerBuffer, 2);

                bool fin = (headerBuffer[0] & 0x80) != 0;
                int opcode = headerBuffer[0] & 0x0F;
                bool masked = (headerBuffer[1] & 0x80) != 0;
                long payloadLen = headerBuffer[1] & 0x7F;

                if (payloadLen == 126)
                {
                    byte[] lenBytes = new byte[2];
                    ReadExact(lenBytes, 2);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    payloadLen = BitConverter.ToUInt16(lenBytes, 0);
                }
                else if (payloadLen == 127)
                {
                    byte[] lenBytes = new byte[8];
                    ReadExact(lenBytes, 8);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    payloadLen = (long)BitConverter.ToUInt64(lenBytes, 0);
                }

                byte[] mask = null;
                if (masked)
                {
                    mask = new byte[4];
                    ReadExact(mask, 4);
                }

                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                    ReadExact(payload, (int)payloadLen);

                if (masked && mask != null)
                {
                    for (int i = 0; i < payload.Length; i++)
                        payload[i] = (byte)(payload[i] ^ mask[i % 4]);
                }

                // Handle fragmented messages (RFC 6455 §5.4):
                // First fragment: opcode != 0, FIN = 0 → start buffering
                // Continuation:   opcode == 0, FIN = 0 → append to buffer
                // Final fragment: opcode == 0, FIN = 1 → complete message
                // Unfragmented:   opcode != 0, FIN = 1 → deliver immediately

                // Control frames (0x8-0xF) may arrive between fragments
                if (opcode >= 0x8)
                {
                    // Control frames are never fragmented, handle immediately
                    switch (opcode)
                    {
                        case 0x8: // Close
                            Close();
                            return;
                        case 0x9: // Ping — respond with pong immediately
                            SendPong(payload);
                            break;
                        case 0xA: // Pong — ignore
                            break;
                    }
                    continue;
                }

                if (opcode != 0)
                {
                    // New data frame
                    if (!fin)
                    {
                        // First fragment - start buffering
                        _fragmentOpcode = opcode;
                        _fragmentBuffer = new List<byte>(payload);
                        continue;
                    }
                    // Unfragmented - deliver directly
                }
                else
                {
                    // Continuation frame (opcode 0)
                    if (_fragmentBuffer == null)
                        continue; // Stray continuation, ignore

                    _fragmentBuffer.AddRange(payload);

                    if (!fin)
                        continue; // More fragments expected

                    // Final fragment - reassemble and deliver
                    opcode = _fragmentOpcode;
                    payload = _fragmentBuffer.ToArray();
                    _fragmentBuffer = null;
                }

                // Deliver complete message
                switch (opcode)
                {
                    case 0x1: // Text
                        OnMessage?.Invoke(Encoding.UTF8.GetString(payload));
                        break;
                    case 0x2: // Binary - ignore
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                Close();
                return;
            }
        }
    }

    private void ReadExact(byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int bytes = _stream.Read(buffer, totalRead, count - totalRead);
            if (bytes == 0) throw new Exception("Connection closed");
            totalRead += bytes;
        }
    }

    public void Close()
    {
        if (!_isConnected) return;
        _isConnected = false;
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        OnClose?.Invoke();
    }
}
