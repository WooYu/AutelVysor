namespace AutelVysor.Models;

public sealed record AndroidAppInfo(
    string PackageName,
    string VersionName,
    string VersionCode,
    string MinSdk,
    string TargetSdk,
    string FirstInstallTime,
    string LastUpdateTime,
    string DataDir,
    string Pid);
