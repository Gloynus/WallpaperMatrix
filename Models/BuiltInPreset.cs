namespace WallpaperMatrix.Models;

/// <summary>
/// An immutable visual reference profile shipped with the application.
/// Built-in profiles deliberately do not own playlists, monitor routes or
/// machine-level switches, so the same profile is safe on any workstation.
/// </summary>
public sealed record BuiltInPreset(
    string Id,
    string Name,
    string Description,
    AppSettings Template);

public static class BuiltInPresetCatalog
{
    public const string IdPrefix = "builtin:";

    private static readonly IReadOnlyList<BuiltInPreset> ItemsInternal =
    [
        CreateCanonical(),
        CreateTerminal(),
        CreateImageArchive(),
        CreateDenseChannel(),
        CreateEconomy()
    ];

    public static IReadOnlyList<BuiltInPreset> Items => ItemsInternal;

    public static BuiltInPreset? Find(string id) =>
        ItemsInternal.FirstOrDefault(profile => string.Equals(
            profile.Id,
            id,
            StringComparison.OrdinalIgnoreCase));

    public static AppSettings Apply(
        BuiltInPreset preset,
        AppSettings current,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        AppSettings result = current.Copy();
        MonitorTopology.EnsureProfiles(result, monitors);
        CopyVisualSettings(result, preset.Template);
        foreach (MonitorProfile profile in result.MonitorProfiles)
        {
            if (profile.WasConnected)
                CopyVisualSettings(profile.Settings, preset.Template);
        }
        result.ActivePresetId = preset.Id;
        result.Normalize();
        return result;
    }

    private static BuiltInPreset CreateCanonical()
    {
        AppSettings template = OperatorDefaults.Create();
        return new BuiltInPreset(
            $"{IdPrefix}canonical",
            "ЭТАЛОН // НАВУХОДОНОСОР",
            "Заводской кинематографический поток: размеренный ритм, длинная память и мягкий импульс.",
            template);
    }

    private static BuiltInPreset CreateTerminal()
    {
        AppSettings template = OperatorDefaults.Create();
        template.FramesPerSecond = 36;
        template.FontSize = 24;
        template.Density = 0.72;
        template.SpeedMin = 0.02;
        template.SpeedMax = 0.26;
        template.TrailLengthMin = 0.14;
        template.TrailLengthMax = 0.95;
        template.MemoryDurationMin = 0.18;
        template.MemoryDurationMax = 1.70;
        template.SignalStrengthMin = 0.25;
        template.SignalStrengthMax = 0.95;
        template.SignalGlowKeys = 0.18;
        template.SignalGlowPriority = 0.72;
        template.HeadBrightness = 0.58;
        template.HeadGlow = 0.75;
        template.HeadImpulseProbability = 0.45;
        template.InterceptionRate = 0.34;
        template.ImageMode = false;
        template.Normalize();
        return new BuiltInPreset(
            $"{IdPrefix}terminal",
            "ЭТАЛОН // ТЕРМИНАЛ",
            "Чистый читаемый код без образов: ровный ритм, умеренные импульсы и аккуратное свечение.",
            template);
    }

    private static BuiltInPreset CreateEconomy()
    {
        AppSettings template = OperatorDefaults.Create();
        template.FramesPerSecond = 24;
        template.FontSize = 32;
        template.Density = 0.46;
        template.SpeedMin = 0.012;
        template.SpeedMax = 0.12;
        template.TrailLengthMin = 0.08;
        template.TrailLengthMax = 0.68;
        template.MemoryDurationMin = 0.08;
        template.MemoryDurationMax = 1.15;
        template.SignalStrengthMin = 0.18;
        template.SignalStrengthMax = 0.68;
        template.SignalGlowKeys = 0.16;
        template.SignalGlowPriority = 0.55;
        template.HeadBrightness = 0.45;
        template.HeadGlow = 0.55;
        template.HeadImpulseProbability = 0.32;
        template.InterceptionRate = 0.20;
        template.StreamLifetimeMin = 0.22;
        template.StreamLifetimeMax = 0.82;
        template.SpeedCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.SpeedKind, "Centered");
        template.TrailLengthCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.LengthKind, "Centered");
        template.SignalCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.SignalKind, "Short");
        template.MemoryCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.MemoryKind, "Short");
        template.ImageMode = false;
        template.Normalize();
        return new BuiltInPreset(
            $"{IdPrefix}economy",
            "ЭТАЛОН // ЭКОНОМИЧНЫЙ",
            "Редкий поток 24 FPS с крупными символами, сдержанным свечением и минимальной фоновой нагрузкой.",
            template);
    }

    private static BuiltInPreset CreateDenseChannel()
    {
        AppSettings template = OperatorDefaults.Create();
        template.FramesPerSecond = 60;
        template.FontSize = 20;
        template.Density = 1.0;
        template.SpeedMin = 0.035;
        template.SpeedMax = 0.48;
        template.TrailLengthMin = 0.24;
        template.TrailLengthMax = 1.0;
        template.MemoryDurationMin = 0.38;
        template.MemoryDurationMax = 2.45;
        template.SignalStrengthMin = 0.32;
        template.SignalStrengthMax = 1.0;
        template.SignalGlowKeys = 0.42;
        template.SignalGlowPriority = 1.25;
        template.HeadBrightness = 0.76;
        template.HeadGlow = 1.35;
        template.HeadImpulseProbability = 0.76;
        template.InterceptionRate = 0.72;
        template.StreamLifetimeMin = 0.56;
        template.StreamLifetimeMax = 1.0;
        template.SpeedCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.SpeedKind, "Extremes");
        template.TrailLengthCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.LengthKind, "Long");
        template.SignalCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.SignalKind, "Extremes");
        template.MemoryCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.MemoryKind, "Long");
        template.ImageMode = false;
        template.Normalize();
        return new BuiltInPreset(
            $"{IdPrefix}dense",
            "ЭТАЛОН // ПЕРЕГРУЗКА",
            "Плотный контрастный канал с частыми перехватами, импульсами и быстрыми выбросами.",
            template);
    }

    private static BuiltInPreset CreateImageArchive()
    {
        AppSettings template = OperatorDefaults.Create();
        template.FramesPerSecond = 36;
        template.FontSize = 24;
        template.Density = 0.90;
        template.SpeedMin = 0.018;
        template.SpeedMax = 0.22;
        template.TrailLengthMin = 0.18;
        template.TrailLengthMax = 1.0;
        template.MemoryDurationMin = 0.36;
        template.MemoryDurationMax = 2.55;
        template.SignalStrengthMin = 0.28;
        template.SignalStrengthMax = 0.82;
        template.ImagePreparationMode = "Custom";
        template.ImageExpressiveness = 0.72;
        template.ImageGlyphMatch = 0.82;
        template.ImageStability = 0.66;
        template.ImageResistance = 0.70;
        template.ImageBrightness = 0.76;
        template.ImageLocalContrast = 0.42;
        template.ImageDetailStrength = 0.86;
        template.ImageEdgeStrength = 0.62;
        template.ImageShadowBalance = 1.08;
        template.ImagePaletteAdaptation = 0.48;
        template.ImageToneCalmness = 0.72;
        template.ImageStructureMode = "Tonal";
        template.ImageMode = true;
        template.SignalCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.SignalKind, "Centered");
        template.MemoryCurve =
            FlowCurveProfiles.Create(FlowCurveProfiles.MemoryKind, "Long");
        template.Normalize();
        return new BuiltInPreset(
            $"{IdPrefix}archive",
            "ЭТАЛОН // АРХИВ ОБРАЗОВ",
            "Ровная тональная передача изображений с усилением деталей и устойчивым светом.",
            template);
    }

    private static void CopyVisualSettings(
        AppSettings target,
        AppSettings source)
    {
        target.SpeedMin = source.SpeedMin;
        target.SpeedMax = source.SpeedMax;
        target.Density = source.Density;
        target.FontSize = source.FontSize;
        target.GlyphStretch = source.GlyphStretch;
        target.GlyphWeight = source.GlyphWeight;
        target.SignalHue = source.SignalHue;
        target.SignalBrightness = source.SignalBrightness;
        target.BackgroundHue = source.BackgroundHue;
        target.BackgroundBrightness = source.BackgroundBrightness;
        target.TrailLengthMin = source.TrailLengthMin;
        target.TrailLengthMax = source.TrailLengthMax;
        target.MemoryDurationMin = source.MemoryDurationMin;
        target.MemoryDurationMax = source.MemoryDurationMax;
        target.SignalStrengthMin = source.SignalStrengthMin;
        target.SignalStrengthMax = source.SignalStrengthMax;
        target.SignalGlowKeys = source.SignalGlowKeys;
        target.SignalGlowPriority = source.SignalGlowPriority;
        target.HeadBrightness = source.HeadBrightness;
        target.HeadGlow = source.HeadGlow;
        target.HeadImpulseDecay = source.HeadImpulseDecay;
        target.HeadImpulseProbability = source.HeadImpulseProbability;
        target.HeadWeight = source.HeadWeight;
        target.InterceptionRate = source.InterceptionRate;
        target.StreamLifetimeMin = source.StreamLifetimeMin;
        target.StreamLifetimeMax = source.StreamLifetimeMax;
        target.SpeedCurve = source.SpeedCurve
            .Select(point => point.Copy())
            .ToList();
        target.TrailLengthCurve = source.TrailLengthCurve
            .Select(point => point.Copy())
            .ToList();
        target.SignalCurve = source.SignalCurve
            .Select(point => point.Copy())
            .ToList();
        target.StreamFilterCurve = source.StreamFilterCurve
            .Select(point => point.Copy())
            .ToList();
        target.MemoryCurve = source.MemoryCurve
            .Select(point => point.Copy())
            .ToList();
        target.SpeedCurveAdjustment =
            source.SpeedCurveAdjustment.Copy();
        target.TrailLengthCurveAdjustment =
            source.TrailLengthCurveAdjustment.Copy();
        target.SignalCurveAdjustment =
            source.SignalCurveAdjustment.Copy();
        target.StreamFilterCurveAdjustment =
            source.StreamFilterCurveAdjustment.Copy();
        target.MemoryCurveAdjustment =
            source.MemoryCurveAdjustment.Copy();
        target.FramesPerSecond = source.FramesPerSecond;
        target.FontFamily = source.FontFamily;
        target.ImageMode = source.ImageMode;
        target.ImageExpressiveness = source.ImageExpressiveness;
        target.ImageGlyphMatch = source.ImageGlyphMatch;
        target.ImageStability = source.ImageStability;
        target.ImageResistance = source.ImageResistance;
        target.ImageBrightness = source.ImageBrightness;
        target.ImagePreparationMode = source.ImagePreparationMode;
        target.ImageLocalContrast = source.ImageLocalContrast;
        target.ImageDetailStrength = source.ImageDetailStrength;
        target.ImageEdgeStrength = source.ImageEdgeStrength;
        target.ImageShadowBalance = source.ImageShadowBalance;
        target.ImagePaletteAdaptation = source.ImagePaletteAdaptation;
        target.ImageToneCalmness = source.ImageToneCalmness;
        target.ImageStructureMode = source.ImageStructureMode;
        target.Normalize(includeMonitorProfiles: false);
    }
}
