namespace WallpaperMatrix.Models;

/// <summary>
/// Stores how one database channel presents one playlist. The playlist
/// catalog remains global; only this lightweight per-playlist override is
/// local to an isolated database.
/// </summary>
public sealed class PlaylistPresentation
{
    public string PlaylistId { get; set; } = "";
    public ImagePlacement Placement { get; set; } = new();

    public PlaylistPresentation Copy() => new()
    {
        PlaylistId = PlaylistId,
        Placement = Placement?.Copy() ?? new ImagePlacement()
    };

    public void Normalize()
    {
        PlaylistId = PlaylistId?.Trim() ?? "";
        Placement ??= new ImagePlacement();
    }
}
