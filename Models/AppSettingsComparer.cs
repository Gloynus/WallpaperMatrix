namespace WallpaperMatrix.Models;

/// <summary>
/// Structural comparison used by the operator console to distinguish a real
/// draft from harmless object copies and floating-point formatting.
/// </summary>
internal static class AppSettingsComparer
{
    private const double Tolerance = 0.0001;

    public static bool Equivalent(AppSettings left, AppSettings right) =>
        Near(left.SpeedMin, right.SpeedMin)
        && Near(left.SpeedMax, right.SpeedMax)
        && Near(left.Density, right.Density)
        && Near(left.FontSize, right.FontSize)
        && Near(left.GlyphStretch, right.GlyphStretch)
        && Near(left.GlyphWeight, right.GlyphWeight)
        && Near(left.SignalHue, right.SignalHue)
        && Near(left.SignalBrightness, right.SignalBrightness)
        && Near(left.BackgroundHue, right.BackgroundHue)
        && Near(left.BackgroundBrightness, right.BackgroundBrightness)
        && Near(left.TrailLengthMin, right.TrailLengthMin)
        && Near(left.TrailLengthMax, right.TrailLengthMax)
        && Near(left.MemoryDurationMin, right.MemoryDurationMin)
        && Near(left.MemoryDurationMax, right.MemoryDurationMax)
        && Near(left.SignalStrengthMin, right.SignalStrengthMin)
        && Near(left.SignalStrengthMax, right.SignalStrengthMax)
        && Near(left.SignalGlowKeys, right.SignalGlowKeys)
        && Near(left.SignalGlowPriority, right.SignalGlowPriority)
        && Near(left.HeadBrightness, right.HeadBrightness)
        && Near(left.HeadGlow, right.HeadGlow)
        && Near(left.HeadImpulseDecay, right.HeadImpulseDecay)
        && Near(left.HeadImpulseProbability, right.HeadImpulseProbability)
        && Near(left.HeadWeight, right.HeadWeight)
        && Near(left.InterceptionRate, right.InterceptionRate)
        && Near(left.StreamLifetimeMin, right.StreamLifetimeMin)
        && Near(left.StreamLifetimeMax, right.StreamLifetimeMax)
        && FlowCurveMath.Equivalent(
            left.SpeedCurve,
            right.SpeedCurve,
            increasing: true,
            tolerance: Tolerance)
        && FlowCurveMath.Equivalent(
            left.TrailLengthCurve,
            right.TrailLengthCurve,
            increasing: true,
            tolerance: Tolerance)
        && FlowCurveMath.Equivalent(
            left.SignalCurve,
            right.SignalCurve,
            increasing: true,
            tolerance: Tolerance)
        && FlowCurveMath.Equivalent(
            left.StreamFilterCurve,
            right.StreamFilterCurve,
            increasing: true,
            tolerance: Tolerance)
        && FlowCurveMath.Equivalent(
            left.MemoryCurve,
            right.MemoryCurve,
            increasing: true,
            tolerance: Tolerance)
        && CurveAdjustmentsEquivalent(
            left.SpeedCurveAdjustment,
            right.SpeedCurveAdjustment)
        && CurveAdjustmentsEquivalent(
            left.TrailLengthCurveAdjustment,
            right.TrailLengthCurveAdjustment)
        && CurveAdjustmentsEquivalent(
            left.SignalCurveAdjustment,
            right.SignalCurveAdjustment)
        && CurveAdjustmentsEquivalent(
            left.StreamFilterCurveAdjustment,
            right.StreamFilterCurveAdjustment)
        && CurveAdjustmentsEquivalent(
            left.MemoryCurveAdjustment,
            right.MemoryCurveAdjustment)
        && left.FramesPerSecond == right.FramesPerSecond
        && left.FontFamily == right.FontFamily
        && left.ImageMode == right.ImageMode
        && left.ActiveImagePlaylistId == right.ActiveImagePlaylistId
        && PlaylistsEquivalent(left.ImagePlaylists, right.ImagePlaylists)
        && Near(left.ImageDurationSeconds, right.ImageDurationSeconds)
        && Near(left.ImageExpressiveness, right.ImageExpressiveness)
        && Near(left.ImageGlyphMatch, right.ImageGlyphMatch)
        && Near(left.ImageStability, right.ImageStability)
        && Near(left.ImageResistance, right.ImageResistance)
        && Near(left.ImageBrightness, right.ImageBrightness)
        && left.ImagePreparationMode == right.ImagePreparationMode
        && Near(left.ImageLocalContrast, right.ImageLocalContrast)
        && Near(left.ImageDetailStrength, right.ImageDetailStrength)
        && Near(left.ImageEdgeStrength, right.ImageEdgeStrength)
        && Near(left.ImageShadowBalance, right.ImageShadowBalance)
        && Near(left.ImagePaletteAdaptation, right.ImagePaletteAdaptation)
        && Near(left.ImageToneCalmness, right.ImageToneCalmness)
        && left.ImageStructureMode == right.ImageStructureMode
        && left.ImageFit == right.ImageFit
        && left.ClockEnabled == right.ClockEnabled
        && left.ClockPosition == right.ClockPosition
        && left.ClockHorizontalMarginCells == right.ClockHorizontalMarginCells
        && left.ClockVerticalMarginCells == right.ClockVerticalMarginCells
        && Near(left.ClockBrightness, right.ClockBrightness)
        && Near(left.ClockWeight, right.ClockWeight)
        && left.StartWithWindows == right.StartWithWindows
        && left.PauseDuringFullscreenApps == right.PauseDuringFullscreenApps
        && left.ActivePresetId == right.ActivePresetId;

    public static bool PresetEquivalent(
        AppSettings settings,
        AppSettings preset)
    {
        AppSettings comparableSettings = settings.Copy();
        AppSettings comparablePreset = preset.Copy();
        comparableSettings.ImagePlaylists = [];
        comparablePreset.ImagePlaylists = [];
        comparableSettings.ActiveImagePlaylistId = "";
        comparablePreset.ActiveImagePlaylistId = "";
        comparableSettings.ActivePresetId = "";
        comparablePreset.ActivePresetId = "";
        return Equivalent(comparableSettings, comparablePreset);
    }

    public static bool ImagePreparationEquivalent(
        AppSettings left,
        AppSettings right) =>
        left.ImagePreparationMode == right.ImagePreparationMode
        && Near(left.ImageLocalContrast, right.ImageLocalContrast)
        && Near(left.ImageDetailStrength, right.ImageDetailStrength)
        && Near(left.ImageEdgeStrength, right.ImageEdgeStrength)
        && Near(left.ImageShadowBalance, right.ImageShadowBalance)
        && Near(left.ImagePaletteAdaptation, right.ImagePaletteAdaptation)
        && left.ImageStructureMode == right.ImageStructureMode;

    private static bool PlaylistsEquivalent(
        IReadOnlyList<ImagePlaylist> left,
        IReadOnlyList<ImagePlaylist> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int playlistIndex = 0; playlistIndex < left.Count; playlistIndex++)
        {
            ImagePlaylist leftPlaylist = left[playlistIndex];
            ImagePlaylist rightPlaylist = right[playlistIndex];
            if (!string.Equals(
                    leftPlaylist.Id,
                    rightPlaylist.Id,
                    StringComparison.Ordinal)
                || !string.Equals(
                    leftPlaylist.Name,
                    rightPlaylist.Name,
                    StringComparison.Ordinal)
                || leftPlaylist.Entries.Count != rightPlaylist.Entries.Count)
            {
                return false;
            }

            for (int entryIndex = 0;
                 entryIndex < leftPlaylist.Entries.Count;
                 entryIndex++)
            {
                ImagePlaylistEntry leftEntry = leftPlaylist.Entries[entryIndex];
                ImagePlaylistEntry rightEntry = rightPlaylist.Entries[entryIndex];
                if (leftEntry.Enabled != rightEntry.Enabled
                    || !string.Equals(
                        leftEntry.Path,
                        rightEntry.Path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool Near(double left, double right) =>
        Math.Abs(left - right) <= Tolerance;

    private static bool CurveAdjustmentsEquivalent(
        CurveAdjustment left,
        CurveAdjustment right) =>
        Near(left.Character, right.Character)
        && Near(left.HorizontalShift, right.HorizontalShift)
        && Near(left.VerticalShift, right.VerticalShift);
}
