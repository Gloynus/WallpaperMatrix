namespace WallpaperMatrix.Services;

internal sealed record AttackInterfaceFrame(
    byte[] Samples,
    int Width,
    int Height,
    int InfluencedSampleCount);

/// <summary>
/// Shared format of the reduced visual map consumed by the attack renderer.
/// The actual desktop capture stays in the D3D11 presenter so the compositor
/// frame and the wallpaper reference never leave the GPU at full resolution.
/// </summary>
internal static class AttackInterfaceCaptureService
{
    internal const int SampleScale = 8;
}
