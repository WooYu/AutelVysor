namespace AutelVysor.Models;

public sealed record AndroidDevice(
    string Serial,
    string State,
    string Model,
    string Product,
    string DeviceName,
    string TransportId,
    string RawDescription,
    int ApiLevel = 0,
    string AndroidVersion = "",
    string Abi = "")
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public bool IsWireless => Serial.Contains(':');
    public string Connection => IsWireless ? "Wi-Fi" : "USB";
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model.Replace('_', ' ');
    public bool IsSupported => ApiLevel >= 19;
    public bool UsesLegacyBackend => ApiLevel is >= 19 and < 21;
    public string Backend => ApiLevel switch
    {
        >= 21 => "scrcpy",
        >= 19 => "ADB Compatibility",
        > 0 => "Unsupported",
        _ => "Unknown"
    };
    public string Description
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(AndroidVersion) ? "Android ?" : $"Android {AndroidVersion}";
            var api = ApiLevel > 0 ? $"API {ApiLevel}" : "API ?";
            var abi = string.IsNullOrWhiteSpace(Abi) ? "ABI ?" : Abi;
            return $"{DisplayName} · {version} · {api} · {abi} · {Connection} · {Backend}";
        }
    }
}
