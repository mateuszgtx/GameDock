using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using WolfControl.Options;

namespace WolfControl.Services;

public sealed class UsbHidPowerOnService : IPowerOnService
{
    private const int OpenWriteOnly = 0x0001;
    private const int OpenNonBlocking = 0x0800;

    private const int ErrorNoEntry = 2;
    private const int ErrorNoDeviceOrAddress = 6;
    private const int ErrorTryAgain = 11;
    private const int ErrorPermissionDenied = 13;
    private const int ErrorNoDevice = 19;
    private const int ErrorBrokenPipe = 32;
    private const int ErrorShutdown = 108;

    private readonly PowerControlOptions _options;
    private readonly ILogger<UsbHidPowerOnService> _logger;

    public UsbHidPowerOnService(
        IOptions<PowerControlOptions> options,
        ILogger<UsbHidPowerOnService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PowerOnResult> PowerOnAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            const string message =
                "USB HID jest obsługiwane tylko na Linuksie z urządzeniem /dev/hidgX.";

            _logger.LogWarning(message);
            return new PowerOnResult(false, message);
        }

        string devicePath = _options.HidDevice.Trim();
        int fileDescriptor = NativeOpen(
            devicePath,
            OpenWriteOnly | OpenNonBlocking);

        if (fileDescriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            string message = BuildOpenErrorMessage(devicePath, error);

            _logger.LogWarning(
                "Nie udało się otworzyć urządzenia USB HID {Device}. errno={Errno}. {Message}",
                devicePath,
                error,
                message);

            return new PowerOnResult(false, message);
        }

        try
        {
            byte keyCode = checked((byte)_options.HidKeyCode);
            byte[] pressReport = new byte[8];
            pressReport[2] = keyCode;

            byte[] releaseReport = new byte[8];

            HidWriteResult press = await WriteReportAsync(
                fileDescriptor,
                pressReport,
                "wciśnięcie klawisza",
                cancellationToken);

            if (!press.Success)
            {
                return BuildFailureResult(devicePath, press);
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(_options.HidPressDurationMs),
                cancellationToken);

            HidWriteResult release = await WriteReportAsync(
                fileDescriptor,
                releaseReport,
                "zwolnienie klawisza",
                cancellationToken);

            if (!release.Success)
            {
                return BuildFailureResult(devicePath, release);
            }

            _logger.LogInformation(
                "Wysłano przez USB HID klawisz 0x{KeyCode:X2} do {Device}.",
                keyCode,
                devicePath);

            return new PowerOnResult(
                true,
                $"Wysłano sygnał USB HID (klawisz 0x{keyCode:X2}).");
        }
        catch (OverflowException ex)
        {
            _logger.LogError(
                ex,
                "Nieprawidłowy kod klawisza HID {KeyCode}.",
                _options.HidKeyCode);

            return new PowerOnResult(
                false,
                "Kod PowerControl:HidKeyCode musi mieścić się w zakresie 1-101.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Nie udało się wysłać raportu USB HID do {Device}.",
                devicePath);

            return new PowerOnResult(
                false,
                $"Nie udało się wysłać sygnału USB HID przez {devicePath}.");
        }
        finally
        {
            if (NativeClose(fileDescriptor) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _logger.LogDebug(
                    "Zamknięcie urządzenia USB HID {Device} zwróciło errno={Errno}.",
                    devicePath,
                    error);
            }
        }
    }

    private async Task<HidWriteResult> WriteReportAsync(
        int fileDescriptor,
        byte[] report,
        string operation,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            _options.HidWriteTimeoutMs);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            nint written = NativeWrite(
                fileDescriptor,
                report,
                (nuint)report.Length);

            if (written == report.Length)
            {
                return HidWriteResult.Ok;
            }

            if (written >= 0)
            {
                return new HidWriteResult(
                    false,
                    0,
                    $"Niepełny zapis raportu HID podczas operacji: {operation}.");
            }

            int error = Marshal.GetLastPInvokeError();

            if (error == ErrorTryAgain
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
                continue;
            }

            string message = error == ErrorTryAgain
                ? $"Host USB nie odebrał raportu HID ({operation}) przed upływem limitu czasu."
                : BuildWriteErrorMessage(operation, error);

            return new HidWriteResult(false, error, message);
        }
    }

    private PowerOnResult BuildFailureResult(
        string devicePath,
        HidWriteResult writeResult)
    {
        _logger.LogWarning(
            "Nie udało się wysłać raportu USB HID do {Device}. errno={Errno}. {Message}",
            devicePath,
            writeResult.ErrorNumber,
            writeResult.Message);

        return new PowerOnResult(false, writeResult.Message);
    }

    private static string BuildOpenErrorMessage(
        string devicePath,
        int error) => error switch
    {
        ErrorNoEntry =>
            $"Nie istnieje urządzenie HID {devicePath}. Sprawdź konfigurację USB Gadget i PowerControl:HidDevice.",
        ErrorPermissionDenied =>
            $"Brak uprawnień do zapisu do {devicePath}. Sprawdź regułę udev i grupy użytkownika usługi.",
        ErrorNoDevice or ErrorNoDeviceOrAddress =>
            $"Urządzenie HID {devicePath} nie jest obecnie dostępne.",
        _ =>
            $"Nie można otworzyć urządzenia HID {devicePath} (errno={error})."
    };

    private static string BuildWriteErrorMessage(
        string operation,
        int error) => error switch
    {
        ErrorShutdown =>
            $"Połączenie USB jest wyłączone przez hosta podczas operacji: {operation}.",
        ErrorBrokenPipe =>
            $"Host USB przerwał połączenie podczas operacji: {operation}.",
        ErrorNoDevice or ErrorNoDeviceOrAddress =>
            $"Host lub urządzenie USB nie jest dostępne podczas operacji: {operation}.",
        ErrorPermissionDenied =>
            $"Brak uprawnień do wysłania raportu HID podczas operacji: {operation}.",
        _ =>
            $"Błąd zapisu USB HID podczas operacji: {operation} (errno={error})."
    };

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int NativeOpen(
        string path,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "write",
        SetLastError = true)]
    private static extern nint NativeWrite(
        int fileDescriptor,
        byte[] buffer,
        nuint count);

    [DllImport(
        "libc",
        EntryPoint = "close",
        SetLastError = true)]
    private static extern int NativeClose(
        int fileDescriptor);

    private sealed record HidWriteResult(
        bool Success,
        int ErrorNumber,
        string Message)
    {
        public static HidWriteResult Ok { get; } =
            new(true, 0, string.Empty);
    }
}
