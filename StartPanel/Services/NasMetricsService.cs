using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed record NasDiskMetrics(
    string Id,
    string Name,
    string? Model,
    string? Serial,
    long? CapacityBytes,
    double? TemperatureCelsius,
    string SmartStatus);

public sealed record NasMetricsStatus(
    bool Available,
    string? HostName,
    string? OperatingSystem,
    DateTimeOffset CheckedAtUtc,
    long? UptimeSeconds,
    double? CpuPercent,
    double? CpuTemperatureCelsius,
    double? SystemTemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? StorageUsedBytes,
    long? StorageTotalBytes,
    string? StoragePoolName,
    string? StorageState,
    long? DiskReadBytesPerSecond,
    long? DiskWriteBytesPerSecond,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    long? NetworkLinkSpeedBitsPerSecond,
    IReadOnlyList<string> NetworkInterfaces,
    int? ActiveConnections,
    IReadOnlyList<NasDiskMetrics> Disks,
    string? Message);

public sealed record AgentNasDiskTelemetry(
    string? Id,
    string? Name,
    string? Model,
    string? Serial,
    long? CapacityBytes,
    double? TemperatureCelsius,
    string? SmartStatus);

public sealed record AgentNasTelemetry(
    string? HostName,
    string? OperatingSystem,
    DateTimeOffset CheckedAtUtc,
    long? UptimeSeconds,
    double? CpuPercent,
    double? CpuTemperatureCelsius,
    double? SystemTemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? StorageUsedBytes,
    long? StorageTotalBytes,
    string? StoragePoolName,
    string? StorageState,
    long? DiskReadBytesPerSecond,
    long? DiskWriteBytesPerSecond,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    long? NetworkLinkSpeedBitsPerSecond,
    IReadOnlyList<string>? NetworkInterfaces,
    int? ActiveConnections,
    IReadOnlyList<AgentNasDiskTelemetry>? Disks);

public sealed class NasMetricsService
{
    private readonly NasOptions _options;
    private readonly NasControlService _nas;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NasMetricsService> _logger;

    public NasMetricsService(
        IOptions<NasOptions> options,
        NasControlService nas,
        IHttpClientFactory httpClientFactory,
        ILogger<NasMetricsService> logger)
    {
        _options = options.Value;
        _nas = nas;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NasMetricsStatus> GetAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Unavailable("Obsługa NAS jest wyłączona.");
        }

        NasStatus status = await _nas.GetStatusAsync(cancellationToken);
        if (!status.Online)
        {
            return Unavailable("NAS jest wyłączony.");
        }

        if (string.IsNullOrWhiteSpace(_options.AgentUrl))
        {
            return Unavailable(
                "Agent NAS nie jest jeszcze skonfigurowany. Ustaw Nas:AgentUrl po przygotowaniu agenta.");
        }

        try
        {
            Uri endpoint = BuildEndpoint(_options.AgentUrl);
            HttpClient client = _httpClientFactory.CreateClient("GameDockNasAgent");
            client.Timeout = TimeSpan.FromMilliseconds(
                Math.Clamp(_options.AgentTimeoutMs, 500, 15_000));

            AgentNasTelemetry? telemetry =
                await client.GetFromJsonAsync<AgentNasTelemetry>(
                    endpoint,
                    cancellationToken);

            if (telemetry is null)
            {
                return Unavailable("Agent NAS zwrócił pustą odpowiedź.");
            }

            IReadOnlyList<NasDiskMetrics> disks = telemetry.Disks?
                .Select((disk, index) => new NasDiskMetrics(
                    Id: string.IsNullOrWhiteSpace(disk.Id)
                        ? $"disk-{index + 1}"
                        : disk.Id,
                    Name: string.IsNullOrWhiteSpace(disk.Name)
                        ? $"Dysk {index + 1}"
                        : disk.Name,
                    Model: disk.Model,
                    Serial: disk.Serial,
                    CapacityBytes: disk.CapacityBytes,
                    TemperatureCelsius: disk.TemperatureCelsius,
                    SmartStatus: string.IsNullOrWhiteSpace(disk.SmartStatus)
                        ? "Unknown"
                        : disk.SmartStatus))
                .ToArray()
                ?? Array.Empty<NasDiskMetrics>();

            return new NasMetricsStatus(
                Available: true,
                HostName: telemetry.HostName,
                OperatingSystem: telemetry.OperatingSystem,
                CheckedAtUtc: telemetry.CheckedAtUtc,
                UptimeSeconds: telemetry.UptimeSeconds,
                CpuPercent: telemetry.CpuPercent,
                CpuTemperatureCelsius: telemetry.CpuTemperatureCelsius,
                SystemTemperatureCelsius: telemetry.SystemTemperatureCelsius,
                MemoryUsedBytes: telemetry.MemoryUsedBytes,
                MemoryTotalBytes: telemetry.MemoryTotalBytes,
                StorageUsedBytes: telemetry.StorageUsedBytes,
                StorageTotalBytes: telemetry.StorageTotalBytes,
                StoragePoolName: telemetry.StoragePoolName,
                StorageState: telemetry.StorageState,
                DiskReadBytesPerSecond: telemetry.DiskReadBytesPerSecond,
                DiskWriteBytesPerSecond: telemetry.DiskWriteBytesPerSecond,
                NetworkRxBytesPerSecond: telemetry.NetworkRxBytesPerSecond,
                NetworkTxBytesPerSecond: telemetry.NetworkTxBytesPerSecond,
                NetworkLinkSpeedBitsPerSecond: telemetry.NetworkLinkSpeedBitsPerSecond,
                NetworkInterfaces: telemetry.NetworkInterfaces ?? Array.Empty<string>(),
                ActiveConnections: telemetry.ActiveConnections,
                Disks: disks,
                Message: null);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested
            && (ex is HttpRequestException
                or TaskCanceledException
                or JsonException
                or UriFormatException))
        {
            _logger.LogDebug(ex, "Nie udało się pobrać metryk agenta NAS.");
            return Unavailable("Agent NAS nie odpowiada.");
        }
    }

    private static Uri BuildEndpoint(string agentUrl)
    {
        var baseUri = new Uri(agentUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, "api/stats");
    }

    private static NasMetricsStatus Unavailable(string message) =>
        new(
            Available: false,
            HostName: null,
            OperatingSystem: null,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            UptimeSeconds: null,
            CpuPercent: null,
            CpuTemperatureCelsius: null,
            SystemTemperatureCelsius: null,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            StorageUsedBytes: null,
            StorageTotalBytes: null,
            StoragePoolName: null,
            StorageState: null,
            DiskReadBytesPerSecond: null,
            DiskWriteBytesPerSecond: null,
            NetworkRxBytesPerSecond: null,
            NetworkTxBytesPerSecond: null,
            NetworkLinkSpeedBitsPerSecond: null,
            NetworkInterfaces: Array.Empty<string>(),
            ActiveConnections: null,
            Disks: Array.Empty<NasDiskMetrics>(),
            Message: message);
}
