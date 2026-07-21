namespace WolfControl.Options;

public sealed class MachineOptions
{
    public const string SectionName = "Machine";

    // Adres używany do pingowania komputera. Dla każdego systemu SSH może mieć
    // osobny adres w polu Systems[].SshHost.
    public string Host { get; init; } = "192.168.0.171";
    public string BroadcastAddress { get; init; } = "192.168.0.255";
    public string MacAddress { get; init; } = "9C:6B:00:59:7B:F1";
    public int WakePort { get; init; } = 9;
    public int PingTimeoutMs { get; init; } = 1200;

    public string RebootCommand { get; init; } =
        "sudo /usr/bin/systemctl reboot";

    public string PowerOffCommand { get; init; } =
        "sudo /usr/bin/systemctl poweroff";

    public string GrubRebootCommand { get; init; } =
        "sudo /usr/bin/grub-reboot";

    public int SystemDetectionCacheSeconds { get; init; } = 12;
    public int AgentTimeoutMs { get; init; } = 4000;

    public List<BootSystemOptions> Systems { get; init; } = new();
}

public sealed class BootSystemOptions
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    // Numer pozycji GRUB liczony od zera, np. 0, 1, 2 albo wpis w podmenu: 1>2.
    public string GrubEntry { get; init; } = string.Empty;

    // Każdy system może mieć własny adres, port, login i hasło SSH.
    // Puste SshHost oznacza użycie Machine:Host.
    public string? SshHost { get; init; }
    public int SshPort { get; init; } = 22;
    public string SshUser { get; init; } = string.Empty;
    public string SshPassword { get; init; } = string.Empty;

    // Adres GameDock.Agent uruchomionego w tym systemie, np.
    // http://192.168.0.171:7070 albo adres Tailscale Windowsa.
    public string? AgentUrl { get; init; }

    // Polecenie wykonywane przez SSH w celu rozpoznania aktualnego systemu.
    public string DetectionCommand { get; init; } = string.Empty;
    public string DetectionContains { get; init; } = string.Empty;

    // Określa, czy z tego systemu można ustawiać kolejny wpis GRUB.
    // Dla Windows zwykle ustaw false.
    public bool CanSwitchFrom { get; init; } = true;

    // Zwykły restart jest niezależny od sterowania GRUB-em.
    // Windows może mieć CanSwitchFrom=false i CanRestart=true.
    public bool CanRestart { get; init; } = true;

    // Opcjonalne komendy specyficzne dla danego systemu.
    public string? RebootCommand { get; init; }
    public string? PowerOffCommand { get; init; }
}
