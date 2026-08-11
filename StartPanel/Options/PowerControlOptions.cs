namespace WolfControl.Options;

public enum PowerStartupMethod
{
    WakeOnLan,
    UsbHid
}

public sealed class PowerControlOptions
{
    public const string SectionName = "PowerControl";

    // WakeOnLan zachowuje dotychczasowe zachowanie aplikacji.
    public PowerStartupMethod StartupMethod { get; init; } =
        PowerStartupMethod.WakeOnLan;

    // Urządzenie znakowe tworzone przez funkcję USB HID Gadget.
    public string HidDevice { get; init; } = "/dev/hidg0";

    // Kod HID Usage ID klawisza wysyłanego jako sygnał uruchomienia.
    // 44 (0x2C) oznacza spację.
    public int HidKeyCode { get; init; } = 44;

    // Czas pomiędzy raportem "wciśnij" i raportem "puść".
    public int HidPressDurationMs { get; init; } = 80;

    // Maksymalny czas prób zapisu pojedynczego raportu. Zapis do hidg
    // wykonywany jest nieblokująco, aby brak hosta USB nie zawieszał aplikacji.
    public int HidWriteTimeoutMs { get; init; } = 1000;
}
