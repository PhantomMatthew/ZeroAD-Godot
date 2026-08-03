using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZeroAD.Godot.Lobby;

/// <summary>STUN 客户端（逐字移植 source/network/StunClient.cpp，~200 行）。
/// RFC 5389 STUN Binding 请求 → 获取公网 IP/port（NAT 穿透）。
/// 原版操作 ENet socket；C# 用 UdpClient（等价 UDP 操作）。</summary>
public static class StunClient
{
    // RFC 5389 常量
    private const uint MagicCookie = 0x2112A442;
    private const ushort MethodTypeBinding = 0x0001;
    private const ushort BindingSuccessResponse = 0x0101;
    private const ushort AttrTypeXORMappedAddress = 0x0020;
    private const byte IPAddressFamilyIPv4 = 0x01;

    private static byte[] s_transactionId = new byte[12];

    /// <summary>发送 STUN 请求并获取公网 IP + port。</summary>
    public static (IPAddress ip, int port)? FindPublicIP(string stunServer, int stunPort, int timeoutMs = 5000)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;

        var stunEndpoint = new IPEndPoint(IPAddress.Parse(stunServer), stunPort);

        // 构造 STUN Binding Request
        var request = new byte[20];
        // Message Type (大端)
        request[0] = (byte)(MethodTypeBinding >> 8);
        request[1] = (byte)(MethodTypeBinding & 0xFF);
        // Message Length = 0（无属性）
        request[2] = 0; request[3] = 0;
        // Magic Cookie
        request[4] = (byte)((MagicCookie >> 24) & 0xFF);
        request[5] = (byte)((MagicCookie >> 16) & 0xFF);
        request[6] = (byte)((MagicCookie >> 8) & 0xFF);
        request[7] = (byte)(MagicCookie & 0xFF);
        // Transaction ID (随机 12 字节)
        var rng = new Random();
        rng.NextBytes(s_transactionId);
        Buffer.BlockCopy(s_transactionId, 0, request, 8, 12);

        // 发送
        udp.Send(request, request.Length, stunEndpoint);

        // 接收
        IPEndPoint? sender = null;
        byte[]? response;
        try { response = udp.Receive(ref sender); }
        catch (SocketException) { return null; }

        if (response == null || response.Length < 20) return null;

        // 验证 Transaction ID
        for (int i = 0; i < 12; i++)
            if (response[8 + i] != s_transactionId[i]) return null;

        // 检查 Binding Success Response
        ushort msgType = (ushort)((response[0] << 8) | response[1]);
        if (msgType != BindingSuccessResponse) return null;

        // 解析 XOR-MAPPED-ADDRESS 属性
        int offset = 20;
        while (offset + 4 <= response.Length)
        {
            ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            ushort attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
            offset += 4;

            if (attrType == AttrTypeXORMappedAddress && offset + attrLen <= response.Length)
            {
                return ParseXORMappedAddress(response, offset, attrLen);
            }

            // 跳过 padding（属性长度非 4 字节对齐时）
            offset += attrLen + ((4 - (attrLen % 4)) % 4);
        }

        return null;
    }

    /// <summary>解析 XOR-MAPPED-ADDRESS（RFC 5389 §15.2）。</summary>
    private static (IPAddress ip, int port)? ParseXORMappedAddress(byte[] buffer, int offset, int len)
    {
        if (len < 8) return null;

        // 跳过 Reserved (1 byte) + Family (1 byte)
        byte family = buffer[offset + 1];
        if (family != IPAddressFamilyIPv4) return null;  // 仅 IPv4

        // XOR Port (2 bytes) — 高 16 位 XOR Magic Cookie 高 16 位
        ushort xorPort = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
        int port = (int)((xorPort ^ (MagicCookie >> 16)) & 0xFFFF);

        // XOR IP (4 bytes) — XOR Magic Cookie
        uint xorIp = (uint)((buffer[offset + 4] << 24) | (buffer[offset + 5] << 16)
                          | (buffer[offset + 6] << 8) | buffer[offset + 7]);
        uint ipVal = xorIp ^ MagicCookie;

        return (new IPAddress((int)ipVal), port);
    }

    /// <summary>获取本地 IP 地址（原版 FindLocalIP）。</summary>
    public static string? FindLocalIP()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var addr in host.AddressList)
            if (addr.AddressFamily == AddressFamily.InterNetwork)
                return addr.ToString();
        return null;
    }
}
