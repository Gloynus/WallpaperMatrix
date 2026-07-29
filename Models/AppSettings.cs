using System.Text.Json.Serialization;

namespace WallpaperMatrix.Models;

public sealed class AppSettings
{
    public const double MinimumSpeed = 0.01;
    public const double MaximumManualSpeed = 10.0;
    public const double MinimumImageDurationSeconds = 0.1;
    public const double MaximumImageDurationSeconds = 600.0;
    public const double MinimumAttackTransitionSeconds = 1.0;
    public const double MaximumAttackTransitionSeconds = 600.0;

    public int SettingsVersion { get; set; } = 33;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Speed { get; set; }
    public double SpeedMin { get; set; } = 0.20;
    public double SpeedMax { get; set; } = 1.0;
    public double Density { get; set; } = 1.0;
    public double FontSize { get; set; } = 24.0;
    public double GlyphStretch { get; set; }
    public double GlyphWeight { get; set; }
    public double SignalHue { get; set; } = SignalColorModel.DefaultHue;
    public double SignalBrightness { get; set; } =
        SignalColorModel.DefaultBrightness;
    public double BackgroundHue { get; set; } = SignalColorModel.DefaultHue;
    public double BackgroundBrightness { get; set; } = 0.03;
    public double TrailLengthMin { get; set; } = 1.0;
    public double TrailLengthMax { get; set; } = 1.0;
    public double MemoryDurationMin { get; set; } = 0.30;
    public double MemoryDurationMax { get; set; } = 0.30;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TrailLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Glow { get; set; }
    public double SignalStrengthMin { get; set; } = 1.0;
    public double SignalStrengthMax { get; set; } = 1.0;
    public double SignalGlowKeys { get; set; } = 1.0;
    public double SignalGlowPriority { get; set; } = 1.0;
    public double HeadBrightness { get; set; } = 0.5;
    public double HeadGlow { get; set; } = 1.0;
    public double HeadImpulseDecay { get; set; } = 0.1;
    public double HeadImpulseProbability { get; set; } = 1.0;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double HeadHighlightLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double HeadPulseFadeSeconds { get; set; }
    public double HeadWeight { get; set; } = 0.5;
    public double InterceptionRate { get; set; }
    public double StreamLifetimeMin { get; set; } = 1.0;
    public double StreamLifetimeMax { get; set; } = 1.0;
    public List<CurvePoint> SpeedCurve { get; set; } =
        FlowCurveProfiles.Create(FlowCurveProfiles.SpeedKind, "Centered");
    public List<CurvePoint> TrailLengthCurve { get; set; } =
        FlowCurveProfiles.Create(FlowCurveProfiles.LengthKind, "Centered");
    public List<CurvePoint> SignalCurve { get; set; } =
        FlowCurveProfiles.DefaultSignal();
    public List<CurvePoint> StreamFilterCurve { get; set; } =
        FlowCurveProfiles.DefaultFilter();
    public List<CurvePoint> MemoryCurve { get; set; } = FlowCurveProfiles.DefaultMemory();
    public CurveAdjustment SpeedCurveAdjustment { get; set; } = new();
    public CurveAdjustment TrailLengthCurveAdjustment { get; set; } = new();
    public CurveAdjustment SignalCurveAdjustment { get; set; } = new();
    public CurveAdjustment StreamFilterCurveAdjustment { get; set; } = new();
    public CurveAdjustment MemoryCurveAdjustment { get; set; } = new();
    public int FramesPerSecond { get; set; } = 24;
    public string FontFamily { get; set; } = "MS Gothic";

    public bool ImageMode { get; set; }
    public List<ImagePlaylist> ImagePlaylists { get; set; } = [new ImagePlaylist()];
    public string ActiveImagePlaylistId { get; set; } = "";
    public string OperatorPlaylistId { get; set; } = "";
    public string OperatorPlaylistName { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ImageFolder { get; set; } = "";
    public double ImageDurationSeconds { get; set; } = 30.0;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ImageIntensity { get; set; }
    public string ImageFit { get; set; } = "Fill";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AdaptiveImageGlyphs { get; set; }
    public double ImageExpressiveness { get; set; } = 0.8;
    public double ImageGlyphMatch { get; set; } = 0.65;
    public double ImageStability { get; set; } = 1.0;
    public double ImageResistance { get; set; } = 0.8;
    public double ImageBrightness { get; set; } = 0.8;
    public string ImagePreparationMode { get; set; } = "Custom";
    public double ImageLocalContrast { get; set; } = 0.5;
    public double ImageDetailStrength { get; set; } = 0.6;
    public double ImageEdgeStrength { get; set; } = 0.5;
    public double ImageShadowBalance { get; set; } = 1.0;
    public double ImagePaletteAdaptation { get; set; } = 0.15;
    public double ImageToneCalmness { get; set; }
    public string ImageStructureMode { get; set; } = "Tonal";

    public bool StartWithWindows { get; set; }
    public bool PauseDuringFullscreenApps { get; set; } = true;
    public bool AttackSystemEnabled { get; set; }
    public double AttackIdleMinutes { get; set; } = 10.0;
    public double AttackTransitionSeconds { get; set; } = 30.0;
    public string VirtualOutputSourceMonitorId { get; set; } = "";
    public int VirtualOutputWidth { get; set; } = 1920;
    public int VirtualOutputHeight { get; set; } = 1080;
    public string VirtualOutputFit { get; set; } = "Fill";
    public bool WelcomeShown { get; set; }
    public string ActivePresetId { get; set; } = "";
    public List<MonitorProfile> MonitorProfiles { get; set; } = [];

    public AppSettings Copy(bool includeMonitorProfiles = true) => new()
    {
        SettingsVersion = SettingsVersion,
        Speed = Speed,
        SpeedMin = SpeedMin,
        SpeedMax = SpeedMax,
        Density = Density,
        FontSize = FontSize,
        GlyphStretch = GlyphStretch,
        GlyphWeight = GlyphWeight,
        SignalHue = SignalHue,
        SignalBrightness = SignalBrightness,
        BackgroundHue = BackgroundHue,
        BackgroundBrightness = BackgroundBrightness,
        TrailLengthMin = TrailLengthMin,
        TrailLengthMax = TrailLengthMax,
        MemoryDurationMin = MemoryDurationMin,
        MemoryDurationMax = MemoryDurationMax,
        Glow = Glow,
        SignalStrengthMin = SignalStrengthMin,
        SignalStrengthMax = SignalStrengthMax,
        SignalGlowKeys = SignalGlowKeys,
        SignalGlowPriority = SignalGlowPriority,
        HeadBrightness = HeadBrightness,
        HeadGlow = HeadGlow,
        HeadImpulseDecay = HeadImpulseDecay,
        HeadImpulseProbability = HeadImpulseProbability,
        HeadHighlightLength = HeadHighlightLength,
        HeadPulseFadeSeconds = HeadPulseFadeSeconds,
        HeadWeight = HeadWeight,
        InterceptionRate = InterceptionRate,
        StreamLifetimeMin = StreamLifetimeMin,
        StreamLifetimeMax = StreamLifetimeMax,
        SpeedCurve = SpeedCurve.Select(point => point.Copy()).ToList(),
        TrailLengthCurve = TrailLengthCurve.Select(point => point.Copy()).ToList(),
        SignalCurve = SignalCurve.Select(point => point.Copy()).ToList(),
        StreamFilterCurve = StreamFilterCurve.Select(point => point.Copy()).ToList(),
        MemoryCurve = MemoryCurve.Select(point => point.Copy()).ToList(),
        SpeedCurveAdjustment = SpeedCurveAdjustment.Copy(),
        TrailLengthCurveAdjustment = TrailLengthCurveAdjustment.Copy(),
        SignalCurveAdjustment = SignalCurveAdjustment.Copy(),
        StreamFilterCurveAdjustment = StreamFilterCurveAdjustment.Copy(),
        MemoryCurveAdjustment = MemoryCurveAdjustment.Copy(),
        FramesPerSecond = FramesPerSecond,
        FontFamily = FontFamily,
        ImageMode = ImageMode,
        ImagePlaylists = ImagePlaylists.Select(playlist => playlist.Copy()).ToList(),
        ActiveImagePlaylistId = ActiveImagePlaylistId,
        OperatorPlaylistId = OperatorPlaylistId,
        OperatorPlaylistName = OperatorPlaylistName,
        ImageFolder = ImageFolder,
        ImageDurationSeconds = ImageDurationSeconds,
        ImageIntensity = ImageIntensity,
        ImageFit = ImageFit,
        AdaptiveImageGlyphs = AdaptiveImageGlyphs,
        ImageExpressiveness = ImageExpressiveness,
        ImageGlyphMatch = ImageGlyphMatch,
        ImageStability = ImageStability,
        ImageResistance = ImageResistance,
        ImageBrightness = ImageBrightness,
        ImagePreparationMode = ImagePreparationMode,
        ImageLocalContrast = ImageLocalContrast,
        ImageDetailStrength = ImageDetailStrength,
        ImageEdgeStrength = ImageEdgeStrength,
        ImageShadowBalance = ImageShadowBalance,
        ImagePaletteAdaptation = ImagePaletteAdaptation,
        ImageToneCalmness = ImageToneCalmness,
        ImageStructureMode = ImageStructureMode,
        StartWithWindows = StartWithWindows,
        PauseDuringFullscreenApps = PauseDuringFullscreenApps,
        AttackSystemEnabled = AttackSystemEnabled,
        AttackIdleMinutes = AttackIdleMinutes,
        AttackTransitionSeconds = AttackTransitionSeconds,
        VirtualOutputSourceMonitorId = VirtualOutputSourceMonitorId,
        VirtualOutputWidth = VirtualOutputWidth,
        VirtualOutputHeight = VirtualOutputHeight,
        VirtualOutputFit = VirtualOutputFit,
        WelcomeShown = WelcomeShown,
        ActivePresetId = ActivePresetId,
        MonitorProfiles = includeMonitorProfiles
            ? MonitorProfiles.Select(profile => profile.Copy()).ToList()
            : []
    };

    public void Normalize(bool includeMonitorProfiles = true)
    {
        if (SettingsVersion < 2)
        {
            if (FramesPerSecond == 36)
                FramesPerSecond = 24;
            SettingsVersion = 2;
        }
        if (SettingsVersion < 3)
        {
            AdaptiveImageGlyphs = true;
            SettingsVersion = 3;
        }
        if (SettingsVersion < 4)
        {
            // Earlier versions exposed an abstract 20–100% control whose
            // actual average occupied only 12–37% of the screen. Preserve
            // that appearance while migrating to a literal screen fraction.
            TrailLength = 0.06 + TrailLength * 0.305;
            SettingsVersion = 4;
        }
        if (SettingsVersion < 5)
        {
            // The former single value was rendered with a natural +/-12%
            // variation. Preserve that exact spread as the new explicit range.
            double center = TrailLength > 0 ? TrailLength : 0.25;
            TrailLengthMin = center * 0.88;
            TrailLengthMax = center * 1.12;
            TrailLength = 0;
            SettingsVersion = 5;
        }
        if (SettingsVersion < 6)
        {
            HeadBrightness = 0.72;
            HeadWeight = 0.34;
            SettingsVersion = 6;
        }
        if (SettingsVersion < 7)
        {
            SettingsVersion = 7;
        }
        if (SettingsVersion < 8)
        {
            PauseDuringFullscreenApps = true;
            SettingsVersion = 8;
        }
        if (SettingsVersion < 9)
        {
            double center = Speed > 0 ? Speed : 1.0;
            SpeedMin = center * 0.8;
            SpeedMax = center * 1.2;
            Speed = 0;
            SettingsVersion = 9;
        }
        if (SettingsVersion < 10)
        {
            // Split the former combined intensity control into independent
            // tonal range, persistence and final screen brightness controls.
            ImageExpressiveness = ImageIntensity > 0 ? ImageIntensity : 0.82;
            ImageGlyphMatch = AdaptiveImageGlyphs ? 1.0 : 0.0;
            ImageStability = 0.72;
            ImageBrightness = 1.0;
            ImageIntensity = 0;
            AdaptiveImageGlyphs = false;
            SettingsVersion = 10;
        }
        if (SettingsVersion < 11)
        {
            HeadHighlightLength = 0;
            ImageResistance = 0.65;
            SettingsVersion = 11;
        }
        if (SettingsVersion < 12)
        {
            TrailLengthCurve = FlowCurveProfiles.DefaultLength();
            ImagePreparationMode = "Auto";
            ImageLocalContrast = 0.5;
            ImageDetailStrength = 0.55;
            ImageEdgeStrength = 0.35;
            ImageShadowBalance = 0.5;
            SettingsVersion = 12;
        }
        if (SettingsVersion < 13)
        {
            List<string> legacyImages = ImagePlaylistCatalog.ExpandPaths([ImageFolder]);
            if (legacyImages.Count > 0)
            {
                ImagePlaylist migrated = new()
                {
                    Name = DirectoryName(ImageFolder),
                    Entries = legacyImages
                        .Select(path => new ImagePlaylistEntry { Path = path })
                        .ToList()
                };
                ImagePlaylists = [migrated];
                ActiveImagePlaylistId = migrated.Id;
            }
            ImageFolder = "";
            SettingsVersion = 13;
        }
        if (SettingsVersion < 14)
        {
            SpeedCurve = FlowCurveProfiles.DefaultSpeed();
            SettingsVersion = 14;
        }
        if (SettingsVersion < 15)
        {
            HeadPulseFadeSeconds = 0.12;
            SettingsVersion = 15;
        }
        if (SettingsVersion < 16)
        {
            if (FlowCurveMath.Equivalent(
                TrailLengthCurve,
                FlowCurveProfiles.LegacyCenteredLength(),
                increasing: true,
                tolerance: 0.002))
            {
                TrailLengthCurve = FlowCurveProfiles.Create(
                    FlowCurveProfiles.LengthKind,
                    "Centered");
            }
            ImagePaletteAdaptation = 0.15;
            ImageStructureMode = "Tonal";
            SettingsVersion = 16;
        }
        if (SettingsVersion < 17)
        {
            TrailLengthMin = Math.Min(TrailLengthMin, 1.0);
            TrailLengthMax = Math.Min(TrailLengthMax, 1.0);
            InterceptionRate = 0.45;
            SettingsVersion = 17;
        }
        if (SettingsVersion < 18)
        {
            StreamLifetimeMin = 1.0;
            StreamLifetimeMax = 1.0;
            StreamFilterCurve = FlowCurveProfiles.DefaultFilter();
            SettingsVersion = 18;
        }
        if (SettingsVersion < 19)
        {
            // Seconds and a fade-shape curve cannot be converted faithfully
            // into the new distance-relative duration distribution. Start the
            // redesigned control at the operator's requested neutral 30%.
            MemoryDurationMin = 0.30;
            MemoryDurationMax = 0.30;
            MemoryCurve = FlowCurveProfiles.DefaultMemory();
            SettingsVersion = 19;
        }
        if (SettingsVersion < 20)
        {
            // Version 1.2 separates the per-stream phosphor signal from the
            // head flash. Preserve the former global glow as a fixed signal
            // range and preserve the former spatial highlight length.
            SignalStrengthMin = Math.Clamp(Glow, 0.0, 1.0);
            SignalStrengthMax = SignalStrengthMin;
            SignalCurve = FlowCurveProfiles.DefaultSignal();
            HeadGlow = 1.0;
            HeadImpulseDecay = Math.Clamp(HeadHighlightLength, 0.0, 1.0);
            Glow = 0;
            HeadHighlightLength = 0;
            HeadPulseFadeSeconds = 0;
            SettingsVersion = 20;
        }
        if (SettingsVersion < 21)
        {
            // Version 1.2 used SignalStrength as halo power. Version 1.3 makes
            // it the discrete base brightness of a stream and gives halo
            // coverage/power their own controls.
            double formerGlow = Math.Clamp(
                (SignalStrengthMin + SignalStrengthMax) * 0.5,
                0.0,
                1.0);
            SignalGlowKeys = formerGlow > 0.001 ? 1.0 : 0.0;
            SignalGlowPriority = formerGlow;
            SignalStrengthMin = 0.90;
            SignalStrengthMax = 0.90;
            HeadImpulseProbability = 1.0;
            SpeedCurveAdjustment = new CurveAdjustment();
            TrailLengthCurveAdjustment = new CurveAdjustment();
            SignalCurveAdjustment = new CurveAdjustment();
            StreamFilterCurveAdjustment = new CurveAdjustment();
            MemoryCurveAdjustment = new CurveAdjustment();
            SettingsVersion = 21;
        }
        if (SettingsVersion < 22)
        {
            // The former control was a multiplier of 10.95 cells per second,
            // so changing the glyph size also changed the apparent screen
            // speed. Preserve its useful 1x-5x range as approximately
            // 20%-100% of the screen height per second.
            SpeedMin *= 0.20;
            SpeedMax *= 0.20;
            SettingsVersion = 22;
        }
        if (SettingsVersion < 23)
        {
            // Version 2.0 exposes the geometry and colour that were previously
            // baked into the atlas and shader. These values reproduce the
            // former semi-bold, natural-height Matrix-green presentation.
            GlyphStretch = 0.0;
            GlyphWeight = 0.4;
            SignalHue = SignalColorModel.DefaultHue;
            SettingsVersion = 23;
        }
        if (SettingsVersion < 24)
        {
            // Version 2.0.1 extends the former 0..100% scale below the
            // original font weight. Existing non-negative values already have
            // the same meaning and therefore require no numeric conversion.
            SettingsVersion = 24;
        }
        if (SettingsVersion < 25)
        {
            // The terminal background used to be derived from the signal hue.
            // Preserve that appearance while making both colours independent.
            BackgroundHue = SignalHue;
            BackgroundBrightness = 0.03;

            // The operator's manually tuned "Мягкая" filter is now represented
            // by one smooth analytical profile instead of nine accidental
            // control points.
            bool adjustmentIsNeutral = StreamFilterCurveAdjustment is null
                || (Math.Abs(StreamFilterCurveAdjustment.Character) < 0.0001
                    && Math.Abs(StreamFilterCurveAdjustment.HorizontalShift) < 0.0001
                    && Math.Abs(StreamFilterCurveAdjustment.VerticalShift) < 0.0001);
            if (adjustmentIsNeutral
                && FlowCurveMath.Equivalent(
                    StreamFilterCurve,
                    FlowCurveProfiles.LegacyOperatorSoftFilter(),
                    increasing: true,
                    tolerance: 0.0001))
            {
                StreamFilterCurve = FlowCurveProfiles.DefaultSoftFilter();
                StreamFilterCurveAdjustment = new CurveAdjustment();
            }
            SettingsVersion = 25;
        }
        if (SettingsVersion < 26)
        {
            // Earlier releases used this exact fixed signal value. Persist it
            // explicitly so upgrading does not change the operator's colours.
            SignalBrightness = SignalColorModel.DefaultBrightness;
            SettingsVersion = 26;
        }
        if (SettingsVersion < 27)
        {
            // 3.2.0 reproduced the old fixed shader value 232/255 exactly,
            // while the UI displayed it as 91%. Snap only that legacy value
            // to the visible control point so Reset and draft comparison agree.
            const double legacySignalBrightness = 232.0 / 255.0;
            if (Math.Abs(SignalBrightness - legacySignalBrightness) < 0.001)
                SignalBrightness = SignalColorModel.DefaultBrightness;
            SettingsVersion = 27;
        }
        if (SettingsVersion < 28)
        {
            AttackSystemEnabled = false;
            AttackIdleMinutes = 10.0;
            AttackTransitionSeconds = 8.0;
            SettingsVersion = 28;
        }
        if (SettingsVersion < 29)
        {
            MonitorProfiles = [];
            SettingsVersion = 29;
        }
        if (SettingsVersion < 30)
            SettingsVersion = 30;
        if (SettingsVersion < 31)
            SettingsVersion = 31;
        if (SettingsVersion < 32)
            SettingsVersion = 32;
        if (SettingsVersion < 33)
            SettingsVersion = 33;
        SpeedMin = Math.Clamp(
            SpeedMin,
            MinimumSpeed,
            MaximumManualSpeed);
        SpeedMax = Math.Clamp(
            SpeedMax,
            MinimumSpeed,
            MaximumManualSpeed);
        if (SpeedMin > SpeedMax)
            (SpeedMin, SpeedMax) = (SpeedMax, SpeedMin);
        SpeedCurve = FlowCurveMath.Normalize(SpeedCurve, increasing: true);
        Density = Math.Clamp(Density, 0.05, 1.0);
        FontSize = Math.Clamp(FontSize, 1.0, 48.0);
        GlyphStretch = Math.Clamp(GlyphStretch, -99.0, 200.0);
        GlyphWeight = Math.Clamp(GlyphWeight, -1.0, 1.0);
        SignalHue = SignalColorModel.NormalizeHue(SignalHue);
        SignalBrightness = Math.Clamp(
            SignalBrightness,
            0.0,
            SignalColorModel.MaximumBrightness);
        BackgroundHue = SignalColorModel.NormalizeHue(BackgroundHue);
        BackgroundBrightness = Math.Clamp(
            BackgroundBrightness,
            0.0,
            SignalColorModel.MaximumBrightness);
        TrailLengthMin = Math.Clamp(TrailLengthMin, 0.0, 1.0);
        TrailLengthMax = Math.Clamp(TrailLengthMax, 0.0, 1.0);
        if (TrailLengthMin > TrailLengthMax)
            (TrailLengthMin, TrailLengthMax) = (TrailLengthMax, TrailLengthMin);
        MemoryDurationMin = Math.Clamp(
            MemoryDurationMin,
            0.0,
            TrailMemoryModel.MaximumDuration);
        MemoryDurationMax = Math.Clamp(
            MemoryDurationMax,
            0.0,
            TrailMemoryModel.MaximumDuration);
        if (MemoryDurationMin > MemoryDurationMax)
            (MemoryDurationMin, MemoryDurationMax) =
                (MemoryDurationMax, MemoryDurationMin);
        SignalStrengthMin = SignalModel.QuantizeStrength(SignalStrengthMin);
        SignalStrengthMax = SignalModel.QuantizeStrength(SignalStrengthMax);
        if (SignalStrengthMin > SignalStrengthMax)
            (SignalStrengthMin, SignalStrengthMax) =
                (SignalStrengthMax, SignalStrengthMin);
        SignalGlowKeys = Math.Clamp(SignalGlowKeys, 0.0, 1.0);
        SignalGlowPriority = Math.Clamp(SignalGlowPriority, 0.0, 2.0);
        HeadBrightness = Math.Clamp(HeadBrightness, 0.0, 1.0);
        HeadGlow = Math.Clamp(HeadGlow, 0.0, 2.0);
        HeadImpulseDecay = Math.Clamp(HeadImpulseDecay, 0.0, 2.0);
        HeadImpulseProbability = Math.Clamp(HeadImpulseProbability, 0.0, 1.0);
        HeadWeight = Math.Clamp(HeadWeight, 0.0, 1.0);
        InterceptionRate = Math.Clamp(InterceptionRate, 0.0, 1.0);
        StreamLifetimeMin = Math.Clamp(StreamLifetimeMin, 0.01, 1.0);
        StreamLifetimeMax = Math.Clamp(StreamLifetimeMax, 0.01, 1.0);
        if (StreamLifetimeMin > StreamLifetimeMax)
            (StreamLifetimeMin, StreamLifetimeMax) =
                (StreamLifetimeMax, StreamLifetimeMin);
        TrailLengthCurve = FlowCurveMath.Normalize(TrailLengthCurve, increasing: true);
        SignalCurve = FlowCurveMath.Normalize(SignalCurve, increasing: true);
        StreamFilterCurve = FlowCurveMath.Normalize(StreamFilterCurve, increasing: true);
        MemoryCurve = FlowCurveMath.Normalize(MemoryCurve, increasing: true);
        SpeedCurveAdjustment ??= new CurveAdjustment();
        TrailLengthCurveAdjustment ??= new CurveAdjustment();
        SignalCurveAdjustment ??= new CurveAdjustment();
        StreamFilterCurveAdjustment ??= new CurveAdjustment();
        MemoryCurveAdjustment ??= new CurveAdjustment();
        SpeedCurveAdjustment.Normalize();
        TrailLengthCurveAdjustment.Normalize();
        SignalCurveAdjustment.Normalize();
        StreamFilterCurveAdjustment.Normalize();
        MemoryCurveAdjustment.Normalize();
        FramesPerSecond = Math.Clamp(FramesPerSecond, 20, 60);
        ImageDurationSeconds = Math.Clamp(
            ImageDurationSeconds,
            MinimumImageDurationSeconds,
            MaximumImageDurationSeconds);
        ImageExpressiveness = Math.Clamp(ImageExpressiveness, 0.0, 1.5);
        ImageGlyphMatch = Math.Clamp(ImageGlyphMatch, 0.0, 1.0);
        ImageStability = Math.Clamp(ImageStability, 0.0, 1.0);
        ImageResistance = Math.Clamp(ImageResistance, 0.0, 1.0);
        ImageBrightness = Math.Clamp(ImageBrightness, 0.0, 1.5);
        ImagePreparationMode = ImagePreparationMode is "None" or "Auto" or "Portrait"
            or "Contours" or "Silhouette" or "Custom"
            ? ImagePreparationMode
            : "Auto";
        ImageLocalContrast = Math.Clamp(ImageLocalContrast, 0.0, 1.0);
        ImageDetailStrength = Math.Clamp(ImageDetailStrength, 0.0, 1.0);
        ImageEdgeStrength = Math.Clamp(ImageEdgeStrength, 0.0, 1.0);
        ImageShadowBalance = Math.Clamp(ImageShadowBalance, 0.0, 1.0);
        ImagePaletteAdaptation = Math.Clamp(ImagePaletteAdaptation, 0.0, 1.0);
        ImageToneCalmness = Math.Clamp(ImageToneCalmness, 0.0, 1.0);
        ImageStructureMode = ImageStructureMode is "Tonal" or "Contours" or "Silhouette"
            ? ImageStructureMode
            : "Tonal";
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "MS Gothic" : FontFamily;
        ImageFolder ??= "";
        ImagePlaylists ??= [];
        HashSet<string> playlistIds = new(StringComparer.OrdinalIgnoreCase);
        ImagePlaylists = ImagePlaylists
            .Where(playlist => playlist is not null)
            .Select(playlist => playlist.Copy())
            .Where(playlist =>
            {
                playlist.Normalize();
                return playlistIds.Add(playlist.Id);
            })
            .Take(64)
            .ToList();
        if (ImagePlaylists.Count == 0)
            ImagePlaylists.Add(new ImagePlaylist());
        if (!ImagePlaylists.Any(playlist =>
            string.Equals(
                playlist.Id,
                ActiveImagePlaylistId,
                StringComparison.OrdinalIgnoreCase)))
        {
            ActiveImagePlaylistId = ImagePlaylists[0].Id;
        }
        ImageFit = ImageFit is "Fill" or "Uniform" ? ImageFit : "Uniform";
        AttackIdleMinutes = Math.Clamp(AttackIdleMinutes, 1.0, 1440.0);
        AttackTransitionSeconds = Math.Clamp(
            AttackTransitionSeconds,
            MinimumAttackTransitionSeconds,
            MaximumAttackTransitionSeconds);
        VirtualOutputSourceMonitorId =
            VirtualOutputSourceMonitorId?.Trim() ?? "";
        VirtualOutputWidth = Math.Clamp(
            VirtualOutputWidth,
            320,
            7680);
        VirtualOutputHeight = Math.Clamp(
            VirtualOutputHeight,
            180,
            4320);
        VirtualOutputFit = VirtualOutputFit is "Fill" or "Uniform"
            ? VirtualOutputFit
            : "Fill";
        ActivePresetId = ActivePresetId?.Trim() ?? "";
        OperatorPlaylistId = OperatorPlaylistId?.Trim() ?? "";
        OperatorPlaylistName = OperatorPlaylistName?.Trim() ?? "";
        MonitorProfiles ??= [];
        if (includeMonitorProfiles)
        {
            HashSet<string> monitorIds =
                new(StringComparer.OrdinalIgnoreCase);
            MonitorProfiles = MonitorProfiles
                .Where(profile => profile is not null)
                .Select(profile =>
                {
                    profile.Normalize();
                    return profile;
                })
                .Where(profile =>
                    !string.IsNullOrWhiteSpace(profile.MonitorId)
                    && monitorIds.Add(profile.MonitorId))
                .Take(32)
                .ToList();
        }
        else
        {
            MonitorProfiles = [];
        }
    }

    public ImagePlaylist ActiveImagePlaylist() =>
        ImagePlaylists.FirstOrDefault(playlist =>
            string.Equals(
                playlist.Id,
                ActiveImagePlaylistId,
                StringComparison.OrdinalIgnoreCase))
        ?? ImagePlaylists[0];

    public string ImagePlaylistSignature()
    {
        ImagePlaylist playlist = ActiveImagePlaylist();
        return playlist.Id + "\n" + string.Join(
            "\n",
            playlist.Entries.Select(entry =>
                (entry.Enabled ? "1|" : "0|") + entry.Path));
    }

    private static string DirectoryName(string folder)
    {
        try
        {
            string trimmed = folder.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            return System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name
                ? name
                : "Архив образов";
        }
        catch
        {
            return "Архив образов";
        }
    }
}
