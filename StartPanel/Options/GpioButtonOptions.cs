namespace WolfControl.Options;

public sealed class GpioButtonOptions
{
    public const string SectionName = "GpioButtons";

    // Wyłączone domyślnie, aby aplikację nadal można było uruchamiać
    // na zwykłym komputerze bez kontrolera GPIO.
    public bool Enabled { get; init; }

    // Częstotliwość odczytu przycisków oraz programowe usuwanie drgań styków.
    public int PollIntervalMilliseconds { get; init; } = 20;
    public int DebounceMilliseconds { get; init; } = 70;

    // Przytrzymanie przycisku systemu przez ten czas wyłącza komputer, ale
    // tylko wtedy, gdy uruchomiony jest system przypisany do przycisku.
    public int ShutdownHoldMilliseconds { get; init; } = 2000;

    public List<GpioSystemButtonOptions> Buttons { get; init; } = new();

    // Osobny przycisk przełączający display-manager na domyślnym Linuksie.
    // Każde naciśnięcie zmienia stan: GUI włączone <-> GUI wyłączone.
    public GpioGraphicalButtonOptions? GraphicalButton { get; init; }
}

public sealed class GpioSystemButtonOptions
{
    // Numer GPIO w schemacie BCM, a nie numer fizycznego pinu złącza.
    public int Pin { get; init; }
    public string SystemId { get; init; } = string.Empty;
    public string? Name { get; init; }
}

public sealed class GpioGraphicalButtonOptions
{
    // Numer GPIO w schemacie BCM. Przycisk zwiera wejście do GND.
    public int Pin { get; init; }
    public string? Name { get; init; } = "GUI";
}
