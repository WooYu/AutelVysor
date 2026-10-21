namespace AutelVysor.Models;

public sealed class AppSettings
{
    public bool AutoReconnectWireless { get; set; } = true;
    public List<string> WirelessEndpoints { get; set; } = [];
    public string MirrorQuality { get; set; } = "Standard";
    public bool EnableAudio { get; set; } = true;
    public bool KeepDeviceAudio { get; set; } = true;
    public bool ClipboardSync { get; set; } = true;
    public bool TurnScreenOffOnMirror { get; set; } = true;
    public bool PreferLegacyAcceleration { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;
}
