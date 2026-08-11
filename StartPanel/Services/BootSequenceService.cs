using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed record BootSequenceStatus(
    bool Active,
    bool Success,
    string Stage,
    string? TargetSystemId,
    string? TargetSystemName,
    string Message,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed class BootSequenceService : BackgroundService
{
    private static readonly Regex GrubEntryPattern = new(
        @"^\d+(>\d+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MachineControlService _machine;
    private readonly MachineOptions _options;
    private readonly ILogger<BootSequenceService> _logger;
    private readonly Channel<string> _requests;
    private readonly object _statusLock = new();

    private BootSequenceStatus _status = new(
        Active: false,
        Success: false,
        Stage: "idle",
        TargetSystemId: null,
        TargetSystemName: null,
        Message: string.Empty,
        StartedAtUtc: null,
        FinishedAtUtc: null);

    public BootSequenceService(
        MachineControlService machine,
        IOptions<MachineOptions> options,
        ILogger<BootSequenceService> logger)
    {
        _machine = machine;
        _options = options.Value;
        _logger = logger;

        _requests = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });
    }

    public BootSequenceStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    public MachineActionResult QueueBoot(string systemId)
    {
        BootSystemOptions? target = FindSystem(systemId);
        if (target is null)
        {
            return new MachineActionResult(
                false,
                "Nie znaleziono wybranego systemu w konfiguracji.");
        }

        BootSystemOptions? bootManager = FindSystem(
            _options.BootManagerSystemId);

        if (bootManager is null)
        {
            return new MachineActionResult(
                false,
                "Machine:BootManagerSystemId nie wskazuje istniejącego systemu.");
        }

        if (!bootManager.CanSwitchFrom)
        {
            return new MachineActionResult(
                false,
                $"System startowy {bootManager.Name} nie ma uprawnień do sterowania GRUB-em.");
        }

        if (!string.Equals(
                target.Id,
                bootManager.Id,
                StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(target.GrubEntry)
                || !GrubEntryPattern.IsMatch(target.GrubEntry)))
        {
            return new MachineActionResult(
                false,
                $"Wpis GRUB dla systemu {target.Name} jest nieprawidłowy.");
        }

        lock (_statusLock)
        {
            if (_status.Active)
            {
                return new MachineActionResult(
                    false,
                    $"Trwa już uruchamianie systemu {_status.TargetSystemName ?? _status.TargetSystemId}.");
            }

            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
            _status = new BootSequenceStatus(
                Active: true,
                Success: false,
                Stage: "queued",
                TargetSystemId: target.Id,
                TargetSystemName: target.Name,
                Message: $"Przygotowano uruchomienie systemu {target.Name}.",
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: null);

            if (!_requests.Writer.TryWrite(target.Id))
            {
                _status = _status with
                {
                    Active = false,
                    Stage = "failed",
                    Message = "Kolejka uruchamiania jest zajęta.",
                    FinishedAtUtc = DateTimeOffset.UtcNow
                };

                return new MachineActionResult(false, _status.Message);
            }
        }

        return new MachineActionResult(
            true,
            $"Rozpoczęto uruchamianie systemu {target.Name}.");
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await foreach (string systemId in _requests.Reader.ReadAllAsync(
                           stoppingToken))
        {
            try
            {
                await ProcessRequestAsync(systemId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Nieobsłużony błąd sekwencji uruchamiania systemu {SystemId}.",
                    systemId);

                Fail("Nie udało się dokończyć sekwencji uruchamiania.");
            }
        }
    }

    private async Task ProcessRequestAsync(
        string systemId,
        CancellationToken cancellationToken)
    {
        BootSystemOptions target = FindSystem(systemId)
            ?? throw new InvalidOperationException(
                "Wybrany system zniknął z konfiguracji.");

        BootSystemOptions bootManager = FindSystem(
            _options.BootManagerSystemId)
            ?? throw new InvalidOperationException(
                "System startowy GRUB zniknął z konfiguracji.");

        TimeSpan timeout = TimeSpan.FromSeconds(
            Math.Clamp(_options.BootSequenceTimeoutSeconds, 30, 900));
        TimeSpan pollDelay = TimeSpan.FromSeconds(
            Math.Clamp(_options.BootSequencePollSeconds, 1, 15));
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        bool grubSelectionSent = false;

        Update(
            "checking",
            $"Sprawdzanie stanu komputera przed uruchomieniem {target.Name}…");

        MachineStatus? machineStatus = await TryGetMachineStatusAsync(
            cancellationToken);

        if (IsTargetRunning(machineStatus, target))
        {
            Complete($"System {target.Name} jest już uruchomiony.");
            return;
        }

        if (machineStatus?.Online == false)
        {
            Update(
                "waking",
                $"Włączanie komputera. Najpierw uruchomi się {bootManager.Name}…");

            MachineActionResult wakeResult = await _machine.WakeAsync(
                cancellationToken);

            if (!wakeResult.Success)
            {
                Fail(wakeResult.Message);
                return;
            }

            Update(
                "waiting-for-linux",
                $"Oczekiwanie na system startowy {bootManager.Name}…");
        }
        else if (machineStatus?.Online == true
                 && machineStatus.CanSwitchSystem)
        {
            if (!await SelectTargetInGrubAsync(target, cancellationToken))
            {
                return;
            }

            grubSelectionSent = true;
        }
        else if (machineStatus?.Online == true
                 && machineStatus.CanRestart)
        {
            Update(
                "restarting-to-linux",
                $"Restart do systemu startowego {bootManager.Name}…");

            MachineActionResult restartResult = await _machine.RestartAsync(
                cancellationToken);

            if (!restartResult.Success)
            {
                Fail(restartResult.Message);
                return;
            }

            Update(
                "waiting-for-linux",
                $"Oczekiwanie na system startowy {bootManager.Name}…");
        }
        else if (machineStatus?.Online == true
                 && machineStatus.CurrentSystemId is not null)
        {
            Fail(
                "Aktualny system nie pozwala ani ustawić GRUB-a, ani wykonać restartu.");
            return;
        }
        else
        {
            Update(
                "waiting-for-linux",
                $"Oczekiwanie na wykrycie systemu startowego {bootManager.Name}…");
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(pollDelay, cancellationToken);

            machineStatus = await TryGetMachineStatusAsync(cancellationToken);
            if (machineStatus is null)
            {
                continue;
            }

            if (IsTargetRunning(machineStatus, target))
            {
                Complete($"Uruchomiono system {target.Name}.");
                return;
            }

            if (!machineStatus.Online || machineStatus.CurrentSystemId is null)
            {
                continue;
            }

            if (grubSelectionSent)
            {
                Update(
                    "waiting-for-target",
                    $"Oczekiwanie na uruchomienie systemu {target.Name}…");
                continue;
            }

            bool bootManagerReady = string.Equals(
                machineStatus.CurrentSystemId,
                bootManager.Id,
                StringComparison.OrdinalIgnoreCase);

            if (!bootManagerReady)
            {
                Update(
                    "waiting-for-linux",
                    $"Wykryto {machineStatus.CurrentSystemName ?? "inny system"}; oczekiwanie na {bootManager.Name}…");
                continue;
            }

            if (string.Equals(
                target.Id,
                bootManager.Id,
                StringComparison.OrdinalIgnoreCase))
            {
                Complete($"Uruchomiono system {target.Name}.");
                return;
            }

            if (!machineStatus.CanSwitchSystem)
            {
                Fail(
                    $"System startowy {bootManager.Name} został wykryty, ale nie może sterować GRUB-em.");
                return;
            }

            if (!await SelectTargetInGrubAsync(target, cancellationToken))
            {
                return;
            }

            grubSelectionSent = true;
        }

        Fail(
            $"Przekroczono czas oczekiwania na uruchomienie systemu {target.Name}.");
    }

    private async Task<bool> SelectTargetInGrubAsync(
        BootSystemOptions target,
        CancellationToken cancellationToken)
    {
        Update(
            "selecting-grub",
            $"Ustawianie {target.Name} jako następnego wpisu GRUB…");

        MachineActionResult bootResult = await _machine.BootSystemAsync(
            target.Id,
            cancellationToken);

        if (!bootResult.Success)
        {
            Fail(bootResult.Message);
            return false;
        }

        Update(
            "waiting-for-target",
            $"GRUB ustawiony. Oczekiwanie na uruchomienie {target.Name}…");
        return true;
    }

    private async Task<MachineStatus?> TryGetMachineStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _machine.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Nie udało się odczytać stanu komputera podczas sekwencji startowej.");
            return null;
        }
    }

    private static bool IsTargetRunning(
        MachineStatus? status,
        BootSystemOptions target) =>
        status?.Online == true
        && string.Equals(
            status.CurrentSystemId,
            target.Id,
            StringComparison.OrdinalIgnoreCase);

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

    private void Update(string stage, string message)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                Active = true,
                Success = false,
                Stage = stage,
                Message = message,
                FinishedAtUtc = null
            };
        }

        _logger.LogInformation("Sekwencja startowa: {Message}", message);
    }

    private void Complete(string message)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                Active = false,
                Success = true,
                Stage = "completed",
                Message = message,
                FinishedAtUtc = DateTimeOffset.UtcNow
            };
        }

        _logger.LogInformation("Sekwencja startowa zakończona: {Message}", message);
    }

    private void Fail(string message)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                Active = false,
                Success = false,
                Stage = "failed",
                Message = message,
                FinishedAtUtc = DateTimeOffset.UtcNow
            };
        }

        _logger.LogWarning("Sekwencja startowa nie powiodła się: {Message}", message);
    }
}
