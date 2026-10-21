namespace AutelVysor.Models;

public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    string Modified)
{
    public string Type => IsDirectory ? "Folder" : "File";
    public string SizeText => IsDirectory ? "" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
    }
}
