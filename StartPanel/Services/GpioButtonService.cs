using System.Device.Gpio;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed record GpioButtonInfo(
    int Pin,
    string Action,
    string? SystemId,
    string Name,
    bool Pressed);

public sealed record GpioButtonStatus(
    bool Enabled,
    bool Available,
    string Message,
    IReadOnlyList<GpioButtonInfo> Buttons);

public sealed class GpioButtonService : BackgroundService
{
    private readonly GpioButtonOptions _options;
    private readonly MachineOptions _machineOptions;
    private readonly MachineControlService _machine;
    private readonly BootSequenceService _bootSequence;
    private readonly ILogger<GpioButtonService> _logger;
    private readonly Channel<ButtonActionRequest> _actions;
    private readonly object _statusLock = new();

    private GpioButtonStatus _status;

    public GpioButtonService(
        IOptions<GpioButtonOptions> options,
        IOptions<MachineOptions> machineOptions,
        MachineControlService machine,
        BootSequenceService bootSequence,
        ILogger<GpioButtonService> logger)
    {
        _options = options.Value;
        _machineOptions = machineOptions.Value;
        _machine = machine;
        _bootSequence = bootSequence;
        _logger = logger;

        _actions = Channel.CreateBounded<ButtonActionRequest>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        _status = new GpioButtonStatus(
            Enabled: _options.Enabled,
            Available: false,
            Message: _options.Enabled
                ? "Kontroler GPIO nie został jeszcze uruchomiony."
                : "Obsługa GPIO jest wyłączona w konfiguracji.",
            Buttons: Array.Empty<GpioButtonInfo>());
    }

    public GpioButtonStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Obsługa fizycznych przycisków GPIO jest wyłączona.");
            return;
        }

        IReadOnlyList<ButtonDefinition> buttons;
        try
        {
            buttons = ValidateConfiguration();
        }
        catch (InvalidOperationException ex)
        {
            SetUnavailable(ex.Message);
            _logger.LogError(ex, "Nieprawidłowa konfiguracja przycisków GPIO.");
            return;
        }

        GpioController? controller = null;
        var states = new List<ButtonState>(buttons.Count);

        try
        {
            controller = new GpioController();

            foreach (ButtonDefinition button in buttons)
            {
                // Przycisk zwiera GPIO do GND. Wewnętrzny pull-up utrzymuje stan
                // wysoki po puszczeniu, a naciśnięcie daje PinValue.Low.
                controller.OpenPin(button.Pin, PinMode.InputPullUp);

                bool pressed = controller.Read(button.Pin) == PinValue.Low;
                states.Add(new ButtonState(
                    button,
                    pressed,
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            controller?.Dispose();
            SetUnavailable($"Nie udało się otworzyć GPIO: {ex.Message}");
            _logger.LogError(ex, "Nie udało się uruchomić kontrolera GPIO.");
            return;
        }

        GpioController gpio = controller!;
        using var controllerLease = gpio;

        UpdateStatus(
            available: true,
            message: "Przyciski GPIO są aktywne.",
            states);

        _logger.LogInformation(
            "Uruchomiono {Count} przycisków GPIO: {Buttons}",
            states.Count,
            string.Join(
                ", ",
                states.Select(state =>
                    $"GPIO{state.Definition.Pin}={state.Definition.Name}")));

        Task actionProcessor = ProcessActionsAsync(stoppingToken);
        int pollInterval = Math.Clamp(
            _options.PollIntervalMilliseconds,
            10,
            250);
        int debounce = Math.Clamp(
            _options.DebounceMilliseconds,
            20,
            500);

        try
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromMilliseconds(pollInterval));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                foreach (ButtonState state in states)
                {
                    bool rawPressed = gpio.Read(state.Definition.Pin)
                        == PinValue.Low;

                    if (rawPressed != state.LastRawPressed)
                    {
                        state.LastRawPressed = rawPressed;
                        state.RawChangedAtUtc = now;
                        continue;
                    }

                    if (rawPressed == state.StablePressed
                        || now - state.RawChangedAtUtc
                            < TimeSpan.FromMilliseconds(debounce))
                    {
                        continue;
                    }

                    state.StablePressed = rawPressed;

                    if (rawPressed)
                    {
                        state.PressedAtUtc = now;
                        _logger.LogDebug(
                            "Naciśnięto przycisk {Name} na GPIO{Pin}.",
                            state.Definition.Name,
                            state.Definition.Pin);
                    }
                    else
                    {
                        TimeSpan heldFor = state.PressedAtUtc.HasValue
                            ? now - state.PressedAtUtc.Value
                            : TimeSpan.Zero;

                        state.PressedAtUtc = null;

                        if (!_actions.Writer.TryWrite(
                                new ButtonActionRequest(
                                    state.Definition,
                                    heldFor)))
                        {
                            _logger.LogWarning(
                                "Pominięto przycisk {Name}, ponieważ kolejka GPIO jest pełna.",
                                state.Definition.Name);
                        }
                    }
                }

                UpdateStatus(
                    available: true,
                    message: "Przyciski GPIO są aktywne.",
                    states);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normalne zatrzymanie usługi.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetUnavailable($"Błąd odczytu GPIO: {ex.Message}");
            _logger.LogError(ex, "Przerwano odczyt przycisków GPIO.");
        }
        finally
        {
            _actions.Writer.TryComplete();

            try
            {
                await actionProcessor;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normalne zatrzymanie procesora akcji.
            }
        }
    }

    private async Task ProcessActionsAsync(CancellationToken stoppingToken)
    {
        await foreach (ButtonActionRequest request in
                       _actions.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await HandleButtonAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Nie udało się obsłużyć przycisku {Name} na GPIO{Pin}.",
                    request.Definition.Name,
                    request.Definition.Pin);
            }
        }
    }

    private async Task HandleButtonAsync(
        ButtonActionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Definition.Action == ButtonAction.GraphicalInterface)
        {
            await HandleGraphicalButtonAsync(request, cancellationToken);
            return;
        }

        await HandleSystemButtonAsync(request, cancellationToken);
    }

    private async Task HandleGraphicalButtonAsync(
        ButtonActionRequest request,
        CancellationToken cancellationToken)
    {
        if (_bootSequence.GetStatus().Active)
        {
            _logger.LogWarning(
                "Pominięto przycisk {Name}, ponieważ trwa zmiana systemu.",
                request.Definition.Name);
            return;
        }

        _logger.LogInformation(
            "Przycisk {Name}: przełączanie interfejsu graficznego.",
            request.Definition.Name);

        MachineActionResult result = await _machine.ToggleGraphicalInterfaceAsync(
            cancellationToken);

        LogResult(
            request.Definition.Name,
            "przełączenie interfejsu graficznego",
            result);
    }

    private async Task HandleSystemButtonAsync(
        ButtonActionRequest request,
        CancellationToken cancellationToken)
    {
        string systemId = request.Definition.SystemId!;
        int shutdownHold = Math.Clamp(
            _options.ShutdownHoldMilliseconds,
            500,
            10000);

        MachineStatus status = await _machine.GetStatusAsync(cancellationToken);
        bool targetIsRunning = status.Online
            && string.Equals(
                status.CurrentSystemId,
                systemId,
                StringComparison.OrdinalIgnoreCase);

        bool longPress = request.HeldFor
            >= TimeSpan.FromMilliseconds(shutdownHold);

        if (targetIsRunning && longPress)
        {
            if (_bootSequence.GetStatus().Active)
            {
                _logger.LogWarning(
                    "Pominięto wyłączenie z przycisku {Name}, ponieważ trwa zmiana systemu.",
                    request.Definition.Name);
                return;
            }

            _logger.LogInformation(
                "Przytrzymano {Name} przez {Duration:F1} s. Wyłączanie systemu {SystemName}.",
                request.Definition.Name,
                request.HeldFor.TotalSeconds,
                status.CurrentSystemName ?? systemId);

            MachineActionResult shutdown = await _machine.ShutdownAsync(
                cancellationToken);

            LogResult(request.Definition.Name, "wyłączenie", shutdown);
            return;
        }

        if (targetIsRunning)
        {
            _logger.LogInformation(
                "System {SystemName} jest już uruchomiony. Przytrzymaj {Name} przez co najmniej {Seconds:F1} s, aby go wyłączyć.",
                status.CurrentSystemName ?? systemId,
                request.Definition.Name,
                shutdownHold / 1000d);
            return;
        }

        MachineActionResult boot = _bootSequence.QueueBoot(systemId);

        LogResult(request.Definition.Name, "uruchomienie systemu", boot);
    }

    private IReadOnlyList<ButtonDefinition> ValidateConfiguration()
    {
        if (_options.Buttons.Count == 0
            && _options.GraphicalButton is null)
        {
            throw new InvalidOperationException(
                "GpioButtons nie zawiera żadnego przycisku.");
        }

        var usedPins = new HashSet<int>();
        var result = new List<ButtonDefinition>();

        foreach (GpioSystemButtonOptions button in _options.Buttons)
        {
            ValidatePin(button.Pin, usedPins);

            if (string.IsNullOrWhiteSpace(button.SystemId))
            {
                throw new InvalidOperationException(
                    $"Przycisk GPIO{button.Pin} nie ma SystemId.");
            }

            BootSystemOptions? system = _machineOptions.Systems.FirstOrDefault(
                configuredSystem => string.Equals(
                    configuredSystem.Id,
                    button.SystemId,
                    StringComparison.OrdinalIgnoreCase));

            if (system is null)
            {
                throw new InvalidOperationException(
                    $"Przycisk GPIO{button.Pin} wskazuje nieistniejący system '{button.SystemId}'.");
            }

            string name = string.IsNullOrWhiteSpace(button.Name)
                ? system.Name
                : button.Name.Trim();

            result.Add(new ButtonDefinition(
                button.Pin,
                name,
                ButtonAction.System,
                system.Id));
        }

        if (_options.GraphicalButton is not null)
        {
            GpioGraphicalButtonOptions graphicalButton =
                _options.GraphicalButton;

            ValidatePin(graphicalButton.Pin, usedPins);

            if (!_machineOptions.GraphicalInterface.Enabled)
            {
                throw new InvalidOperationException(
                    "Skonfigurowano GpioButtons:GraphicalButton, ale Machine:GraphicalInterface:Enabled ma wartość false.");
            }

            string targetSystemId = string.IsNullOrWhiteSpace(
                _machineOptions.GraphicalInterface.SystemId)
                ? _machineOptions.BootManagerSystemId.Trim()
                : _machineOptions.GraphicalInterface.SystemId.Trim();

            bool targetExists = _machineOptions.Systems.Any(system =>
                string.Equals(
                    system.Id,
                    targetSystemId,
                    StringComparison.OrdinalIgnoreCase));

            if (!targetExists)
            {
                throw new InvalidOperationException(
                    $"Przycisk GUI wskazuje nieistniejący system '{targetSystemId}'.");
            }

            string name = string.IsNullOrWhiteSpace(graphicalButton.Name)
                ? "GUI"
                : graphicalButton.Name.Trim();

            result.Add(new ButtonDefinition(
                graphicalButton.Pin,
                name,
                ButtonAction.GraphicalInterface,
                targetSystemId));
        }

        return result;
    }

    private static void ValidatePin(int pin, HashSet<int> usedPins)
    {
        if (pin is < 0 or > 53)
        {
            throw new InvalidOperationException(
                $"GPIO{pin} jest poza obsługiwanym zakresem 0-53.");
        }

        if (!usedPins.Add(pin))
        {
            throw new InvalidOperationException(
                $"GPIO{pin} został przypisany więcej niż raz.");
        }
    }

    private void LogResult(
        string buttonName,
        string operation,
        MachineActionResult result)
    {
        if (result.Success)
        {
            _logger.LogInformation(
                "Przycisk {Name}: {Operation}: {Message}",
                buttonName,
                operation,
                result.Message);
        }
        else
        {
            _logger.LogWarning(
                "Przycisk {Name}: nieudane {Operation}: {Message}",
                buttonName,
                operation,
                result.Message);
        }
    }

    private void UpdateStatus(
        bool available,
        string message,
        IReadOnlyList<ButtonState> states)
    {
        GpioButtonInfo[] buttons = states
            .Select(state => new GpioButtonInfo(
                state.Definition.Pin,
                state.Definition.Action == ButtonAction.System
                    ? "system"
                    : "graphical-interface",
                state.Definition.SystemId,
                state.Definition.Name,
                state.StablePressed))
            .ToArray();

        lock (_statusLock)
        {
            _status = new GpioButtonStatus(
                Enabled: true,
                Available: available,
                Message: message,
                Buttons: buttons);
        }
    }

    private void SetUnavailable(string message)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                Available = false,
                Message = message
            };
        }
    }

    private enum ButtonAction
    {
        System,
        GraphicalInterface
    }

    private sealed record ButtonDefinition(
        int Pin,
        string Name,
        ButtonAction Action,
        string? SystemId);

    private sealed class ButtonState
    {
        public ButtonState(
            ButtonDefinition definition,
            bool initialPressed,
            DateTimeOffset now)
        {
            Definition = definition;
            LastRawPressed = initialPressed;
            StablePressed = initialPressed;
            RawChangedAtUtc = now;
            PressedAtUtc = initialPressed ? now : null;
        }

        public ButtonDefinition Definition { get; }
        public bool LastRawPressed { get; set; }
        public bool StablePressed { get; set; }
        public DateTimeOffset RawChangedAtUtc { get; set; }
        public DateTimeOffset? PressedAtUtc { get; set; }
    }

    private sealed record ButtonActionRequest(
        ButtonDefinition Definition,
        TimeSpan HeldFor);
}
