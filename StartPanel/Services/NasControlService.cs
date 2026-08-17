using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using WolfControl.Options;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace WolfControl.Services;

public sealed record NasStatus(
    bool Enabled,
    string Name,
    bool Online,
    long? RoundtripTimeMs,
    DateTimeOffset CheckedAtUtc,
    string State,
    string? Message);

public sealed record NasActionResult(
    bool Success,
    string Message);

public sealed class NasControlService
{
    private readonly NasOptions _options;
    private readonly WakeOnLanService _wakeOnLan;
    private readonly ILogger<NasControlService> _logger;
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public NasControlService(
        IOptions<NasOptions> options,
        WakeOnLanService wakeOnLan,
        ILogger<NasControlService> logger)
    {
        _options = options.Value;
        _wakeOnLan = wakeOnLan;
        _logger = logger;
    }

    public async Task<NasStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new NasStatus(
                Enabled: false,
                Name: _options.Name,
                Online: false,
                RoundtripTimeMs: null,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                State: "disabled",
                Message: "Obsługa NAS jest wyłączona w konfiguracji.");
        }

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            return Unknown("Nie ustawiono Nas:Host.");
        }

        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(
                _options.Host,
                Math.Clamp(_options.PingTimeoutMs, 250, 10_000));

            cancellationToken.ThrowIfCancellationRequested();

            bool online = reply.Status == IPStatus.Success;
            return new NasStatus(
                Enabled: true,
                Name: _options.Name,
                Online: online,
                RoundtripTimeMs: online ? reply.RoundtripTime : null,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                State: online ? "online" : "offline",
                Message: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is PingException
            or InvalidOperationException
            or ArgumentException)
        {
            _logger.LogDebug(
                ex,
                "Nie udało się sprawdzić stanu NAS {NasHost}.",
                _options.Host);

            return Unknown("Nie udało się sprawdzić stanu NAS.");
        }
    }

    public async Task<NasActionResult> WakeAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Fail("Obsługa NAS jest wyłączona.");
        }

        if (string.IsNullOrWhiteSpace(_options.MacAddress)
            || string.IsNullOrWhiteSpace(_options.BroadcastAddress))
        {
            return Fail("Ustaw Nas:MacAddress i Nas:BroadcastAddress.");
        }

        if (!await _actionGate.WaitAsync(0, cancellationToken))
        {
            return Fail("Inna operacja NAS jest już wykonywana.");
        }

        try
        {
            await _wakeOnLan.SendAsync(
                _options.MacAddress,
                _options.BroadcastAddress,
                _options.WakePort,
                cancellationToken);

            _logger.LogInformation(
                "Wysłano Wake-on-LAN do NAS {NasName} ({Mac}).",
                _options.Name,
                _options.MacAddress);

            return Ok("Wysłano pakiet Wake-on-LAN do NAS.");
        }
        catch (Exception ex) when (
            ex is FormatException
            or ArgumentException
            or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex, "Nie udało się wysłać Wake-on-LAN do NAS.");
            return Fail($"Nie udało się wysłać Wake-on-LAN: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public Task<NasActionResult> ShutdownAsync(
        CancellationToken cancellationToken) =>
        RunSshCommandAsync(
            _options.ShutdownCommand,
            "Wyłączanie NAS zostało zlecone.",
            cancellationToken);

    public Task<NasActionResult> RestartAsync(
        CancellationToken cancellationToken) =>
        RunSshCommandAsync(
            _options.RestartCommand,
            "Restart NAS został zlecony.",
            cancellationToken);

    private async Task<NasActionResult> RunSshCommandAsync(
        string command,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Fail("Obsługa NAS jest wyłączona.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail("Nie skonfigurowano polecenia SSH dla NAS.");
        }

        if (string.IsNullOrWhiteSpace(_options.SshUser))
        {
            return Fail("Nie ustawiono Nas:SshUser.");
        }

        if (!await _actionGate.WaitAsync(0, cancellationToken))
        {
            return Fail("Inna operacja NAS jest już wykonywana.");
        }

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using SshClient client = CreateSshClient();
                client.Connect();

                using SshCommand sshCommand = client.CreateCommand(command);
                sshCommand.CommandTimeout = TimeSpan.FromSeconds(
                    Math.Clamp(_options.SshTimeoutSeconds, 2, 60));

                string output = sshCommand.Execute();
                int exitStatus = sshCommand.ExitStatus ?? -1;
                string error = sshCommand.Error;

                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // Przy poweroff/reboot host może zerwać połączenie od razu.
                }

                if (exitStatus != 0)
                {
                    string detail = string.IsNullOrWhiteSpace(error)
                        ? output
                        : error;
                    return Fail(
                        $"Polecenie NAS zakończyło się kodem {exitStatus}: {detail}".Trim());
                }

                return Ok(successMessage);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is SshException
            or System.Net.Sockets.SocketException
            or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Nie udało się wykonać polecenia SSH na NAS.");
            return Fail($"Nie udało się połączyć z NAS przez SSH: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private SshClient CreateSshClient()
    {
        string host = string.IsNullOrWhiteSpace(_options.SshHost)
            ? _options.Host
            : _options.SshHost;

        var auth = new PasswordAuthenticationMethod(
            _options.SshUser,
            _options.SshPassword ?? string.Empty);

        var connectionInfo = new SshConnectionInfo(
            host,
            _options.SshPort,
            _options.SshUser,
            auth)
        {
            Timeout = TimeSpan.FromSeconds(
                Math.Clamp(_options.SshTimeoutSeconds, 2, 60))
        };

        return new SshClient(connectionInfo);
    }

    private NasStatus Unknown(string message) =>
        new(
            Enabled: _options.Enabled,
            Name: _options.Name,
            Online: false,
            RoundtripTimeMs: null,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            State: "unknown",
            Message: message);

    private static NasActionResult Ok(string message) => new(true, message);
    private static NasActionResult Fail(string message) => new(false, message);
}
