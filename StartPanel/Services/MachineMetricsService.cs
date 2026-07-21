using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed record GpuMetrics(
    int Index,
    string Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius,
    double? PowerWatts);

public sealed record WolfSessionMetrics(
    string? SessionId,
    string? Client,
    string? Application);

public sealed record WolfMetrics(
    bool Enabled,
    bool Available,
    int ActiveSessions,
    int MaxSessions,
    int FreeSlots,
    IReadOnlyList<WolfSessionMetrics> Sessions,
    string? Message);

public sealed record MachineMetricsStatus(
    bool Available,
    string? SystemId,
    string? HostName,
    string? OperatingSystem,
    DateTimeOffset CheckedAtUtc,
    long? UptimeSeconds,
    double? CpuPercent,
    double? CpuTemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    long? NetworkRxTotalBytes,
    long? NetworkTxTotalBytes,
    long? NetworkLinkSpeedBitsPerSecond,
    IReadOnlyList<string> NetworkInterfaces,
    IReadOnlyList<GpuMetrics> Gpus,
    WolfMetrics? Wolf,
    string? Message);

public sealed record AgentGpuTelemetry(
    int Index,
    string? Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius,
    double? PowerWatts);

public sealed record AgentWolfSessionTelemetry(
    string? SessionId,
    string? Client,
    string? Application);

public sealed record AgentWolfTelemetry(
    bool Enabled,
    bool Available,
    int ActiveSessions,
    int MaxSessions,
    int FreeSlots,
    IReadOnlyList<AgentWolfSessionTelemetry>? Sessions,
    string? Message);

public sealed record AgentTelemetry(
    string? SystemId,
    string? HostName,
    string? OperatingSystem,
    DateTimeOffset CheckedAtUtc,
    long? UptimeSeconds,
    double? CpuPercent,
    double? CpuTemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    long? NetworkRxTotalBytes,
    long? NetworkTxTotalBytes,
    long? NetworkLinkSpeedBitsPerSecond,
    IReadOnlyList<string>? NetworkInterfaces,
    IReadOnlyList<AgentGpuTelemetry>? Gpus,
    AgentWolfTelemetry? Wolf);

public sealed class MachineMetricsService
{
    private readonly MachineOptions _options;
    private readonly MachineControlService _machine;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MachineMetricsService> _logger;

    public MachineMetricsService(
        IOptions<MachineOptions> options,
        MachineControlService machine,
        IHttpClientFactory httpClientFactory,
        ILogger<MachineMetricsService> logger)
    {
        _options = options.Value;
        _machine = machine;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<MachineMetricsStatus> GetAsync(
        CancellationToken cancellationToken)
    {
        MachineStatus status = await _machine.GetStatusAsync(cancellationToken);

        if (!status.Online)
        {
            return Unavailable("Komputer jest wyłączony.");
        }

        if (string.IsNullOrWhiteSpace(status.CurrentSystemId))
        {
            return Unavailable("Nie wykryto aktualnego systemu.");
        }

        BootSystemOptions? system = _options.Systems.FirstOrDefault(item =>
            string.Equals(
                item.Id,
                status.CurrentSystemId,
                StringComparison.OrdinalIgnoreCase));

        if (system is null || string.IsNullOrWhiteSpace(system.AgentUrl))
        {
            return Unavailable(
                "Dla aktualnego systemu nie ustawiono AgentUrl.",
                status.CurrentSystemId);
        }

        try
        {
            Uri endpoint = BuildEndpoint(system.AgentUrl);
            HttpClient client = _httpClientFactory.CreateClient("GameDockAgent");
            client.Timeout = TimeSpan.FromMilliseconds(
                Math.Clamp(_options.AgentTimeoutMs, 500, 15_000));

            AgentTelemetry? telemetry = await client.GetFromJsonAsync<AgentTelemetry>(
                endpoint,
                cancellationToken);

            if (telemetry is null)
            {
                return Unavailable("Agent zwrócił pustą odpowiedź.", status.CurrentSystemId);
            }

            if (!string.IsNullOrWhiteSpace(telemetry.SystemId)
                && !string.Equals(
                    telemetry.SystemId,
                    status.CurrentSystemId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "GameDock.Agent pod adresem {AgentUrl} zgłosił SystemId={AgentSystemId}, a wykryty system to {CurrentSystemId}.",
                    system.AgentUrl,
                    telemetry.SystemId,
                    status.CurrentSystemId);
            }

            IReadOnlyList<GpuMetrics> gpus = telemetry.Gpus?
                .Select(gpu => new GpuMetrics(
                    gpu.Index,
                    gpu.Name ?? $"GPU {gpu.Index}",
                    gpu.UtilizationPercent,
                    gpu.MemoryUsedBytes,
                    gpu.MemoryTotalBytes,
                    gpu.TemperatureCelsius,
                    gpu.PowerWatts))
                .ToArray()
                ?? Array.Empty<GpuMetrics>();

            WolfMetrics? wolf = telemetry.Wolf is null
                ? null
                : new WolfMetrics(
                    Enabled: telemetry.Wolf.Enabled,
                    Available: telemetry.Wolf.Available,
                    ActiveSessions: telemetry.Wolf.ActiveSessions,
                    MaxSessions: telemetry.Wolf.MaxSessions,
                    FreeSlots: telemetry.Wolf.FreeSlots,
                    Sessions: telemetry.Wolf.Sessions?
                        .Select(session => new WolfSessionMetrics(
                            session.SessionId,
                            session.Client,
                            session.Application))
                        .ToArray()
                        ?? Array.Empty<WolfSessionMetrics>(),
                    Message: telemetry.Wolf.Message);

            return new MachineMetricsStatus(
                Available: true,
                SystemId: status.CurrentSystemId,
                HostName: telemetry.HostName,
                OperatingSystem: telemetry.OperatingSystem,
                CheckedAtUtc: telemetry.CheckedAtUtc,
                UptimeSeconds: telemetry.UptimeSeconds,
                CpuPercent: telemetry.CpuPercent,
                CpuTemperatureCelsius: telemetry.CpuTemperatureCelsius,
                MemoryUsedBytes: telemetry.MemoryUsedBytes,
                MemoryTotalBytes: telemetry.MemoryTotalBytes,
                DiskUsedBytes: telemetry.DiskUsedBytes,
                DiskTotalBytes: telemetry.DiskTotalBytes,
                NetworkRxBytesPerSecond: telemetry.NetworkRxBytesPerSecond,
                NetworkTxBytesPerSecond: telemetry.NetworkTxBytesPerSecond,
                NetworkRxTotalBytes: telemetry.NetworkRxTotalBytes,
                NetworkTxTotalBytes: telemetry.NetworkTxTotalBytes,
                NetworkLinkSpeedBitsPerSecond: telemetry.NetworkLinkSpeedBitsPerSecond,
                NetworkInterfaces: telemetry.NetworkInterfaces ?? Array.Empty<string>(),
                Gpus: gpus,
                Wolf: wolf,
                Message: null);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested
            && (ex is HttpRequestException
                or TaskCanceledException
                or JsonException
                or UriFormatException))
        {
            _logger.LogDebug(
                ex,
                "Nie udało się pobrać metryk GameDock.Agent dla systemu {SystemId}.",
                status.CurrentSystemId);

            return Unavailable(
                "Agent metryk nie odpowiada.",
                status.CurrentSystemId);
        }
    }

    private static Uri BuildEndpoint(string agentUrl)
    {
        var baseUri = new Uri(agentUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, "api/stats");
    }

    private static MachineMetricsStatus Unavailable(
        string message,
        string? systemId = null) =>
        new(
            Available: false,
            SystemId: systemId,
            HostName: null,
            OperatingSystem: null,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            UptimeSeconds: null,
            CpuPercent: null,
            CpuTemperatureCelsius: null,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            DiskUsedBytes: null,
            DiskTotalBytes: null,
            NetworkRxBytesPerSecond: null,
            NetworkTxBytesPerSecond: null,
            NetworkRxTotalBytes: null,
            NetworkTxTotalBytes: null,
            NetworkLinkSpeedBitsPerSecond: null,
            NetworkInterfaces: Array.Empty<string>(),
            Gpus: Array.Empty<GpuMetrics>(),
            Wolf: null,
            Message: message);
}
