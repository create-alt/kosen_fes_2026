using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

internal sealed class UdpTrackingInput : IDisposable
{
    [Serializable]
    private sealed class Packet
    {
        public float forward = float.NaN;
        public float head = float.NaN;
        public float punch = -1f;
    }

    private const int MaxPacketsPerFrame = 64;
    private const double InputTimeout = 0.5;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly UdpClient client;
    private IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
    private double lastPacketTime = double.NegativeInfinity;
    private double nextReceiveLogTime;
    private bool punchHeld;

    public UdpTrackingInput(int port)
    {
        client = new UdpClient(port);
        client.Client.Blocking = false;
    }

    public bool Read(PlayerController player)
    {
        double now = Time.unscaledTimeAsDouble;
        for (int index = 0; index < MaxPacketsPerFrame; index++)
        {
            byte[] bytes;
            try
            {
                if (!client.Client.Poll(0, SelectMode.SelectRead)) break;
                bytes = client.Receive(ref sender);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock || exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                break;
            }

            if (bytes.Length == 0 || bytes.Length > 2048)
            {
                LogReceived(now, $"無効なサイズ ({bytes.Length} バイト)", player);
                continue;
            }
            Packet packet = new Packet();
            string json = null;
            try
            {
                json = Utf8.GetString(bytes);
                JsonUtility.FromJsonOverwrite(json, packet);
            }
            catch (ArgumentException)
            {
                LogReceived(now, json == null ? "UTF-8として読み取れません" : $"無効なJSON {json}", player);
                continue;
            }

            bool validPose = packet.forward >= 0f && packet.forward <= 1f && packet.head >= 0f && packet.head <= 1f;
            bool validPunch = packet.punch == 0f || packet.punch == 1f;
            if (!validPose || !validPunch)
            {
                LogReceived(now, $"項目または値が仕様外 {json}", player);
                continue;
            }

            LogReceived(now, json, player);

            lastPacketTime = now;
            bool pressed = packet.punch == 1f && !punchHeld;
            punchHeld = packet.punch == 1f;
            if (player.GameplayEnabled) player.ApplyUdpTracking(packet.forward, packet.head, pressed);
        }

        return now - lastPacketTime <= InputTimeout;
    }

    private void LogReceived(double now, string message, PlayerController player)
    {
        if (now < nextReceiveLogTime) return;
        nextReceiveLogTime = now + 1d;
        Debug.Log($"[UDP受信] {sender} → {message}", player);
    }

    public void Dispose()
    {
        client.Dispose();
    }
}
