namespace WolfControl.Options;

public sealed class NasOptions
{
    public const string SectionName = "Nas";

    public bool Enabled { get; init; }
    public string Name { get; init; } = "NAS";

    public string Host { get; init; } = string.Empty;
    public string BroadcastAddress { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public int WakePort { get; init; } = 9;
    public int PingTimeoutMs { get; init; } = 1200;

    public string? SshHost { get; init; }
    public int SshPort { get; init; } = 22;
    public string SshUser { get; init; } = string.Empty;
    public string SshPassword { get; init; } = string.Empty;
    public string ShutdownCommand { get; init; } = "sudo -n /usr/bin/systemctl poweroff";
    public string RestartCommand { get; init; } = "sudo -n /usr/bin/systemctl reboot";
    public int SshTimeoutSeconds { get; init; } = 8;

    public string? AgentUrl { get; init; }
    public int AgentTimeoutMs { get; init; } = 4000;
}
