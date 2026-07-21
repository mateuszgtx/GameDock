using System.Net;
using System.Net.Sockets;

namespace WolfControl.Services;

public sealed class WakeOnLanService
{
    public async Task SendAsync(
        string macAddress,
        string broadcastAddress,
        int port,
        CancellationToken cancellationToken)
    {
        byte[] mac = ParseMacAddress(macAddress);
        byte[] packet = BuildMagicPacket(mac);
        var endpoint = new IPEndPoint(IPAddress.Parse(broadcastAddress), port);

        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await udp.SendAsync(packet, packet.Length, endpoint);
            await Task.Delay(200, cancellationToken);
        }
    }

    private static byte[] ParseMacAddress(string value)
    {
        string normalized = value
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.Length != 12)
        {
            throw new FormatException(
                "Adres MAC musi zawierać 12 znaków szesnastkowych.");
        }

        return Convert.FromHexString(normalized);
    }

    private static byte[] BuildMagicPacket(byte[] mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException(
                "Adres MAC musi mieć 6 bajtów.",
                nameof(mac));
        }

        byte[] packet = new byte[6 + 16 * mac.Length];
        Array.Fill(packet, (byte)0xFF, 0, 6);

        for (int i = 0; i < 16; i++)
        {
            Buffer.BlockCopy(
                mac,
                0,
                packet,
                6 + i * mac.Length,
                mac.Length);
        }

        return packet;
    }
}
