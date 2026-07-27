namespace WallpaperMatrix.Models;

public sealed class OperatorPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    public AppSettings Settings { get; set; } = new();
    internal string FilePath { get; set; } = "";

    public string ModifiedLabel =>
        ModifiedAt.ToString("yyyy.MM.dd  HH:mm:ss");
}
