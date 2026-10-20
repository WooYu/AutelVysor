namespace AutelVysor.Models;

public sealed class AppSettings
{
    public bool AutoReconnectWireless { get; set; } = true;
    public List<string> WirelessEndpoints { get; set; } = [];
}
