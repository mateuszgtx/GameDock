using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed class WakeOnLanService : IPowerOnService
{
    private readonly MachineOptions _options;
    private readonly ILogger<WakeOnLanService> _logger;

    public WakeOnLanService(
        IOptions<MachineOptions> options,
        ILogger<WakeOnLanService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PowerOnResult> PowerOnAsync(
        CancellationToken cancellationToken)
    {
        await SendAsync(
            _options.MacAddress,
            _options.BroadcastAddress,
            _options.WakePort,
            cancellationToken);

        _logger.LogInformation(
            "Wysłano pakiet Wake-on-LAN dla {Mac}",
            _options.MacAddress);

        return new PowerOnResult(
            true,
            "Wysłano pakiet Wake-on-LAN.");
    }

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
