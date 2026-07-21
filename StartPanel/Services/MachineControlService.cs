using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using WolfControl.Options;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace WolfControl.Services;

public sealed record BootSystemInfo(
    string Id,
    string Name,
    bool Active);

public sealed record MachineStatus(
    bool Online,
    long? RoundtripTimeMs,
    DateTimeOffset CheckedAtUtc,
    string State,
    string? CurrentSystemId,
    string? CurrentSystemName,
    bool CanRestart,
    bool CanSwitchSystem,
    IReadOnlyList<BootSystemInfo> Systems);

public sealed record MachineActionResult(
    bool Success,
    string Message);

public sealed class MachineControlService
{
    private static readonly Regex GrubEntryPattern = new(
        @"^\d+(>\d+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MachineOptions _options;
    private readonly WakeOnLanService _wakeOnLan;
    private readonly ILogger<MachineControlService> _logger;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly object _systemCacheLock = new();

    private bool _hasSystemCache;
    private string? _cachedSystemId;
    private DateTimeOffset _systemCacheExpiresAtUtc = DateTimeOffset.MinValue;

    public MachineControlService(
        IOptions<MachineOptions> options,
        WakeOnLanService wakeOnLan,
        ILogger<MachineControlService> logger)
    {
        _options = options.Value;
        _wakeOnLan = wakeOnLan;
        _logger = logger;
    }

    public async Task<MachineStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var ping = new Ping();

        try
        {
            PingReply reply = await ping.SendPingAsync(
                _options.Host,
                _options.PingTimeoutMs);

            bool online = reply.Status == IPStatus.Success;
            if (!online)
            {
                ClearSystemCache();
                return BuildStatus(false, null, "offline", null);
            }

            string? currentSystemId = null;

            if (_options.Systems.Count > 0)
            {
                try
                {
                    currentSystemId = await DetectCurrentSystemAsync(
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Komputer odpowiada na ping, ale nie udało się wykryć systemu przez SSH.");
                }
            }

            return BuildStatus(
                true,
                reply.RoundtripTime,
                "online",
                currentSystemId);
        }
        catch (Exception ex) when (
            ex is PingException
            or InvalidOperationException
            or ArgumentException)
        {
            _logger.LogWarning(
                ex,
                "Nie udało się spingować hosta {Host}",
                _options.Host);

            return BuildStatus(false, null, "unknown", null);
        }
    }

    public async Task<MachineActionResult> WakeAsync(
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken);

        try
        {
            ClearSystemCache();

            await _wakeOnLan.SendAsync(
                _options.MacAddress,
                _options.BroadcastAddress,
                _options.WakePort,
                cancellationToken);

            _logger.LogInformation(
                "Wysłano pakiet Wake-on-LAN dla {Mac}",
                _options.MacAddress);

            return new MachineActionResult(
                true,
                "Wysłano pakiet Wake-on-LAN.");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async Task<MachineActionResult> RestartAsync(
        CancellationToken cancellationToken)
    {
        string? currentSystemId = await DetectCurrentSystemAsync(
            cancellationToken);

        BootSystemOptions? current = FindSystem(currentSystemId);
        if (current is null)
        {
            return new MachineActionResult(
                false,
                "Nie udało się wykryć aktualnego systemu. Reset z panelu jest zablokowany.");
        }

        if (!current.CanRestart)
        {
            return new MachineActionResult(
                false,
                $"Reset z panelu jest zablokowany dla systemu {current.Name}.");
        }

        string command = ResolveSystemCommand(
            current,
            system => system.RebootCommand,
            _options.RebootCommand);

        return await RunSshCommandAsync(
            current,
            command,
            "Wysłano polecenie restartu.",
            cancellationToken,
            clearSystemCache: true);
    }

    public async Task<MachineActionResult> ShutdownAsync(
        CancellationToken cancellationToken)
    {
        string? currentSystemId = await DetectCurrentSystemAsync(
            cancellationToken);

        BootSystemOptions? current = FindSystem(currentSystemId);
        if (current is null)
        {
            return new MachineActionResult(
                false,
                "Nie udało się wykryć aktualnego systemu. Wyłączenie z panelu jest zablokowane.");
        }

        string command = ResolveSystemCommand(
            current,
            system => system.PowerOffCommand,
            _options.PowerOffCommand);

        return await RunSshCommandAsync(
            current,
            command,
            "Wysłano polecenie wyłączenia.",
            cancellationToken,
            clearSystemCache: true);
    }

    public async Task<MachineActionResult> BootSystemAsync(
        string systemId,
        CancellationToken cancellationToken)
    {
        BootSystemOptions? target = FindSystem(systemId);

        if (target is null)
        {
            return new MachineActionResult(
                false,
                "Nie znaleziono wybranego systemu w konfiguracji.");
        }

        if (string.IsNullOrWhiteSpace(target.GrubEntry)
            || !GrubEntryPattern.IsMatch(target.GrubEntry))
        {
            return new MachineActionResult(
                false,
                $"Wpis GRUB dla systemu {target.Name} jest nieprawidłowy.");
        }

        string? currentSystemId = await DetectCurrentSystemAsync(
            cancellationToken);
        BootSystemOptions? current = FindSystem(currentSystemId);

        if (current is null)
        {
            return new MachineActionResult(
                false,
                "Nie udało się wykryć aktualnie uruchomionego systemu.");
        }

        if (!current.CanSwitchFrom)
        {
            return new MachineActionResult(
                false,
                $"Z systemu {current.Name} nie można sterować GRUB-em.");
        }

        if (string.Equals(
            currentSystemId,
            target.Id,
            StringComparison.OrdinalIgnoreCase))
        {
            return new MachineActionResult(
                false,
                $"System {target.Name} jest już uruchomiony.");
        }

        if (string.IsNullOrWhiteSpace(_options.GrubRebootCommand))
        {
            return new MachineActionResult(
                false,
                "Brakuje Machine:GrubRebootCommand w konfiguracji.");
        }

        string rebootCommand = ResolveSystemCommand(
            current,
            system => system.RebootCommand,
            _options.RebootCommand);

        string command =
            $"{_options.GrubRebootCommand} {QuotePosixArgument(target.GrubEntry)}" +
            $" && {rebootCommand}";

        return await RunSshCommandAsync(
            current,
            command,
            $"Wybrano {target.Name}. Komputer uruchamia się ponownie.",
            cancellationToken,
            clearSystemCache: true);
    }

    private MachineStatus BuildStatus(
        bool online,
        long? roundtripTimeMs,
        string state,
        string? currentSystemId)
    {
        BootSystemOptions? current = FindSystem(currentSystemId);

        IReadOnlyList<BootSystemInfo> systems = _options.Systems
            .Where(system =>
                !string.IsNullOrWhiteSpace(system.Id)
                && !string.IsNullOrWhiteSpace(system.Name))
            .Select(system => new BootSystemInfo(
                system.Id,
                system.Name,
                string.Equals(
                    system.Id,
                    currentSystemId,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return new MachineStatus(
            Online: online,
            RoundtripTimeMs: roundtripTimeMs,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            State: state,
            CurrentSystemId: current?.Id,
            CurrentSystemName: current?.Name,
            CanRestart: online && current is not null && current.CanRestart,
            CanSwitchSystem: online && current is not null && current.CanSwitchFrom,
            Systems: systems);
    }

    private async Task<string?> DetectCurrentSystemAsync(
        CancellationToken cancellationToken)
    {
        if (TryGetCachedSystemId(out string? cachedSystemId))
        {
            return cachedSystemId;
        }

        await _actionGate.WaitAsync(cancellationToken);

        try
        {
            if (TryGetCachedSystemId(out cachedSystemId))
            {
                return cachedSystemId;
            }

            string? detectedSystemId = await Task.Run(
                DetectCurrentSystem,
                cancellationToken);

            SetSystemCache(detectedSystemId);
            return detectedSystemId;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private string? DetectCurrentSystem()
    {
        foreach (BootSystemOptions system in _options.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.Id)
                || string.IsNullOrWhiteSpace(system.DetectionCommand)
                || string.IsNullOrWhiteSpace(system.DetectionContains))
            {
                continue;
            }

            try
            {
                using SshClient client = CreateSshClient(system);
                client.Connect();

                CommandResult result = ExecuteDetectionCommand(
                    client,
                    system.DetectionCommand);

                client.Disconnect();

                if (result.ExitStatus == 0
                    && result.Output.Contains(
                        system.DetectionContains,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Wykryto system {SystemId} przez SSH na {Host}:{Port}.",
                        system.Id,
                        ResolveSshHost(system),
                        system.SshPort);

                    return system.Id;
                }

                if (result.ExitStatus != 0)
                {
                    _logger.LogWarning(
                        "Komenda wykrywania systemu {SystemId} zakończyła się kodem {ExitStatus}: {Output}",
                        system.Id,
                        result.ExitStatus,
                        result.Output);
                }
                else
                {
                    _logger.LogDebug(
                        "System {SystemId} odpowiedział przez SSH, ale wynik nie zawiera tekstu '{Expected}'. Wynik: {Output}",
                        system.Id,
                        system.DetectionContains,
                        result.Output);
                }
            }
            catch (Exception ex) when (
                ex is SshException
                or SocketException
                or InvalidOperationException)
            {
                _logger.LogWarning(
                    "Nie udało się sprawdzić systemu {SystemId} przez SSH na {Host}:{Port}: {Error}",
                    system.Id,
                    ResolveSshHost(system),
                    system.SshPort,
                    ex.Message);
            }
        }

        return null;
    }

    private static CommandResult ExecuteDetectionCommand(
        SshClient client,
        string command)
    {
        try
        {
            using SshCommand sshCommand = client.CreateCommand(command);
            sshCommand.CommandTimeout = TimeSpan.FromSeconds(6);

            string output = sshCommand.Execute();
            string combinedOutput = string.Join(
                Environment.NewLine,
                new[] { output, sshCommand.Error }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            return new CommandResult(
                sshCommand.ExitStatus ?? -1,
                combinedOutput);
        }
        catch (SshException ex)
        {
            return new CommandResult(-1, ex.Message);
        }
    }

    private async Task<MachineActionResult> RunSshCommandAsync(
        BootSystemOptions system,
        string command,
        string successMessage,
        CancellationToken cancellationToken,
        bool clearSystemCache)
    {
        await _actionGate.WaitAsync(cancellationToken);

        try
        {
            MachineActionResult result = await Task.Run(
                () => ExecuteSshCommand(system, command, successMessage),
                cancellationToken);

            if (clearSystemCache)
            {
                ClearSystemCache();
            }

            return result;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private MachineActionResult ExecuteSshCommand(
        BootSystemOptions system,
        string command,
        string successMessage)
    {
        using SshClient client = CreateSshClient(system);
        client.Connect();

        using SshCommand sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromSeconds(12);

        string output = sshCommand.Execute();
        int exitStatus = sshCommand.ExitStatus ?? -1;

        if (exitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Polecenie SSH zakończyło się kodem " +
                $"{exitStatus}: {sshCommand.Error}");
        }

        _logger.LogInformation(
            "Wykonano polecenie SSH na systemie {SystemId}, hoście {Host}. Wynik: {Output}",
            system.Id,
            ResolveSshHost(system),
            output.Trim());

        client.Disconnect();

        return new MachineActionResult(true, successMessage);
    }

    private SshClient CreateSshClient(BootSystemOptions system)
    {
        ValidateSshConfiguration(system);

        string host = ResolveSshHost(system);

        var connectionInfo = new SshConnectionInfo(
            host,
            system.SshPort,
            system.SshUser,
            new PasswordAuthenticationMethod(
                system.SshUser,
                system.SshPassword))
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        var client = new SshClient(connectionInfo);

        // GameDock działa w prywatnej sieci VPN/LAN i nie wymaga
        // ręcznego wpisywania fingerprintu hosta SSH.
        client.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = true;
        };

        return client;
    }

    private void ValidateSshConfiguration(BootSystemOptions system)
    {
        string systemPath = string.IsNullOrWhiteSpace(system.Id)
            ? "Machine:Systems"
            : $"Machine:Systems:{system.Id}";

        if (string.IsNullOrWhiteSpace(ResolveSshHost(system)))
        {
            throw new InvalidOperationException(
                $"Brakuje {systemPath}:SshHost albo Machine:Host w konfiguracji.");
        }

        if (system.SshPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Nieprawidłowy {systemPath}:SshPort w konfiguracji.");
        }

        if (string.IsNullOrWhiteSpace(system.SshUser))
        {
            throw new InvalidOperationException(
                $"Brakuje {systemPath}:SshUser w konfiguracji.");
        }

        if (string.IsNullOrWhiteSpace(system.SshPassword))
        {
            throw new InvalidOperationException(
                $"Brakuje {systemPath}:SshPassword w konfiguracji.");
        }

    }

    private string ResolveSshHost(BootSystemOptions system) =>
        string.IsNullOrWhiteSpace(system.SshHost)
            ? _options.Host
            : system.SshHost.Trim();

    private static string ResolveSystemCommand(
        BootSystemOptions current,
        Func<BootSystemOptions, string?> commandSelector,
        string fallback)
    {
        string? systemCommand = commandSelector(current);

        return string.IsNullOrWhiteSpace(systemCommand)
            ? fallback
            : systemCommand;
    }

    private BootSystemOptions? FindSystem(string? systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return null;
        }

        return _options.Systems.FirstOrDefault(system =>
            string.Equals(
                system.Id,
                systemId,
                StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetCachedSystemId(out string? systemId)
    {
        lock (_systemCacheLock)
        {
            if (_hasSystemCache
                && _systemCacheExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                systemId = _cachedSystemId;
                return true;
            }

            systemId = null;
            return false;
        }
    }

    private void SetSystemCache(string? systemId)
    {
        lock (_systemCacheLock)
        {
            _hasSystemCache = true;
            _cachedSystemId = systemId;
            _systemCacheExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(
                Math.Clamp(_options.SystemDetectionCacheSeconds, 3, 120));
        }
    }

    private void ClearSystemCache()
    {
        lock (_systemCacheLock)
        {
            _hasSystemCache = false;
            _cachedSystemId = null;
            _systemCacheExpiresAtUtc = DateTimeOffset.MinValue;
        }
    }

    private static string QuotePosixArgument(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private sealed record CommandResult(
        int ExitStatus,
        string Output);
}
