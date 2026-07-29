namespace WallpaperMatrix.Models;

// Kept as a single source of the immutable first-run profile for callers that
// need to compare or reset one field. The UI deliberately exposes no global
// reset operation.
public static class OperatorDefaults
{
    public static AppSettings Create()
    {
        ImagePlaylist playlist = new();
        AppSettings settings = new()
        {
            SettingsVersion = 33,
            SpeedMin = 0.01,
            SpeedMax = 0.20,
            Density = 0.80,
            FontSize = 30,
            GlyphStretch = 10,
            GlyphWeight = 0,
            SignalHue = SignalColorModel.DefaultHue,
            SignalBrightness = SignalColorModel.DefaultBrightness,
            BackgroundHue = SignalColorModel.DefaultHue,
            BackgroundBrightness = 0.03,
            TrailLengthMin = 0.10,
            TrailLengthMax = 1.0,
            MemoryDurationMin = 0.10,
            MemoryDurationMax = 2.0,
            SignalStrengthMin = 0.25,
            SignalStrengthMax = 1.0,
            SignalGlowKeys = 0.30,
            SignalGlowPriority = 1.0,
            HeadBrightness = 0.60,
            HeadGlow = 1.0,
            HeadImpulseDecay = 0.06,
            HeadImpulseProbability = 0.60,
            HeadWeight = 0.50,
            InterceptionRate = 0.40,
            StreamLifetimeMin = 0.30,
            StreamLifetimeMax = 1.0,
            SpeedCurve = CenteredCurve(),
            TrailLengthCurve = LongCurve(),
            SignalCurve = LongCurve(),
            StreamFilterCurve = FlowCurveProfiles.DefaultSoftFilter(),
            MemoryCurve = CenteredCurve(),
            SpeedCurveAdjustment = new CurveAdjustment(),
            TrailLengthCurveAdjustment = new CurveAdjustment(),
            SignalCurveAdjustment = new CurveAdjustment(),
            StreamFilterCurveAdjustment = new CurveAdjustment(),
            MemoryCurveAdjustment = new CurveAdjustment(),
            FramesPerSecond = 24,
            FontFamily = "MS Gothic",
            ImageMode = false,
            ImagePlaylists = [playlist],
            ActiveImagePlaylistId = playlist.Id,
            ImageFolder = "",
            ImageDurationSeconds = 30,
            ImageFit = "Fill",
            ImageExpressiveness = 0.90,
            ImageGlyphMatch = 0.65,
            ImageStability = 0.30,
            ImageResistance = 0.60,
            ImageBrightness = 0.80,
            ImagePreparationMode = "Custom",
            ImageLocalContrast = 0.50,
            ImageDetailStrength = 0.60,
            ImageEdgeStrength = 0.50,
            ImageShadowBalance = 1.0,
            ImagePaletteAdaptation = 0.10,
            ImageToneCalmness = 0.50,
            ImageStructureMode = "Tonal",
            StartWithWindows = false,
            PauseDuringFullscreenApps = true,
            AttackSystemEnabled = false,
            AttackIdleMinutes = 10,
            AttackTransitionSeconds = 30,
            WelcomeShown = false
        };
        settings.Normalize();
        return settings;
    }

    private static List<CurvePoint> CenteredCurve() =>
    [
        new(0, 0),
        new(0.125, 0.2420052252568245),
        new(0.25, 0.3984684504554705),
        new(0.375, 0.4793826888941735),
        new(0.5, 0.5),
        new(0.625, 0.5206173111058264),
        new(0.75, 0.6015315495445295),
        new(0.875, 0.7579947747431754),
        new(1, 1)
    ];

    private static List<CurvePoint> LongCurve() =>
    [
        new(0, 0),
        new(0.125, 0.21365252707221516),
        new(0.25, 0.40418658941004315),
        new(0.375, 0.5708747877710478),
        new(0.5, 0.7128254112507413),
        new(0.625, 0.8288975288425359),
        new(0.75, 0.9175307555766941),
        new(0.875, 0.976316928648275),
        new(1, 1)
    ];
}
