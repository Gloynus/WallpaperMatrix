using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WallpaperMatrix.Models;
using WallpaperMatrix.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfCursors = System.Windows.Input.Cursors;

namespace WallpaperMatrix.Views;

public partial class SettingsWindow : Window
{
    private sealed class PresetChoice
    {
        public string Label { get; init; } = "ОПЕРАТОР";
        public OperatorPreset? Preset { get; init; }
        public BuiltInPreset? BuiltInPreset { get; init; }
        public string Id => Preset?.Id ?? BuiltInPreset?.Id ?? "";
        public string Details => BuiltInPreset?.Description
            ?? (Preset is null
                ? "Параметры не связаны с глобальным пресетом"
                : $"Пользовательский пресет // изменён: {Preset.ModifiedLabel}");
    }

    private sealed class MonitorRouteChoice
    {
        public required MonitorLinkMode Mode { get; init; }
        public string SourceMonitorId { get; init; } = "";
        public required string Label { get; init; }
    }

    private enum MonitorBadgeKind
    {
        FlowRelay,
        FlowExtend,
        DatabaseIsolated,
        DatabaseRelay,
        DatabaseExtend
    }

    private enum WheelGestureTarget
    {
        MainForm,
        Playlist,
        MonitorTopology,
        FocusedInput
    }

    private AppSettings _source = new();
    private AppSettings _draftSettings = new();
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private string _selectedMonitorId = "";
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _fontPreviewTimer;
    private readonly DispatcherTimer _colorPreviewTimer;
    private readonly DispatcherTimer _virtualResolutionTimer;
    private readonly DispatcherTimer _sectionNavigationTimer;
    private readonly Stopwatch _sectionNavigationClock = new();
    private double _sectionNavigationStartOffset;
    private double _sectionNavigationTargetOffset;
    private bool _updatingSectionNavigation;
    private double _livePreviewFontSize = 24;
    private double _livePreviewGlyphStretch;
    private double _livePreviewGlyphWeight;
    private double _speedMinValue = 0.20;
    private double _speedMaxValue = 1.0;
    private double _memoryMinValue = 0.30;
    private double _memoryMaxValue = 0.30;
    private double _imageDurationSecondsValue = 30.0;
    private double _attackIdleMinutesValue = 10.0;
    private double _attackTransitionSecondsValue = 8.0;
    private List<CurvePoint> _speedCurve = FlowCurveProfiles.DefaultSpeed();
    private List<CurvePoint> _lengthCurve = FlowCurveProfiles.DefaultLength();
    private List<CurvePoint> _signalCurve = FlowCurveProfiles.DefaultSignal();
    private List<CurvePoint> _filterCurve = FlowCurveProfiles.DefaultFilter();
    private List<CurvePoint> _memoryCurve = FlowCurveProfiles.DefaultMemory();
    private readonly Dictionary<string, CurveAdjustment> _curveAdjustments =
        new(StringComparer.Ordinal)
        {
            [FlowCurveProfiles.SpeedKind] = new CurveAdjustment(),
            [FlowCurveProfiles.LengthKind] = new CurveAdjustment(),
            [FlowCurveProfiles.SignalKind] = new CurveAdjustment(),
            [FlowCurveProfiles.FilterKind] = new CurveAdjustment(),
            [FlowCurveProfiles.MemoryKind] = new CurveAdjustment()
        };
    private List<ImagePlaylist> _playlists = [new ImagePlaylist()];
    private string _activePlaylistId = "";
    private bool _sortNameDescending;
    private bool _sortOldestFirst;
    private bool _allowClose;
    private bool _loading = true;
    private bool _hasPendingChanges;
    private bool _wallpaperPaused;
    private readonly HashSet<string> _openOutputMonitorIds =
        new(StringComparer.OrdinalIgnoreCase);
    private ComboBox? _virtualMonitorWidthCombo;
    private ComboBox? _virtualMonitorHeightCombo;
    private Border? _virtualMonitorVisual;
    private const double TopologyDeviceScale = 0.14;
    private const double TopologyPadding = 32.0;
    private const double MinimumTopologyZoom = 0.02;
    private double _topologyZoom = 1.0;
    private double _topologyOriginX;
    private double _topologyOriginY;
    private bool _topologyAutoFit = true;
    private bool _topologyFitQueued;
    private bool _topologyPanning;
    private bool _topologyPanCandidate;
    private MouseButton _topologyPanButton;
    private System.Windows.Point _topologyPanStart;
    private double _topologyPanHorizontalOffset;
    private double _topologyPanVerticalOffset;
    private bool _virtualMonitorDragging;
    private System.Windows.Point _virtualMonitorDragStart;
    private double _virtualMonitorDragLeft;
    private double _virtualMonitorDragTop;
    private long _lastWheelTick;
    private WheelGestureTarget _wheelGestureTarget =
        WheelGestureTarget.MainForm;
    private string _runtimeStatus = "СОСТОЯНИЕ ВЫВОДА НЕ ПОЛУЧЕНО";
    private string _diagnosticLogPath = DiagnosticLog.LogPath;
    private readonly PresetStore _presetStore = new();
    private readonly PlaylistStore _playlistStore = new();
    private string _playlistFileVersion = "";
    private List<OperatorPreset> _presets = [];
    private string _selectedPresetId = "";
    private bool _updatingPresetUi;
    private int _externalImageLaunchInProgress;

    public event Action<AppSettings>? SettingsApplied;
    public event Action<AppSettings>? PlaylistsSaved;
    public event Action<AppSettings>? PlaylistsReloaded;
    public event Action<AppSettings>? SettingsPreviewed;
    public event Action<AppSettings, string, string>? ImageRequested;
    public event Action<bool>? PauseRequested;
    public event Action? AttackRequested;
    public event Action<AppSettings, bool>? VirtualOutputRequested;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = $"{AppVersion.DisplayName} — Operator Console";
        VersionText.Text =
            $"OPERATOR CONSOLE // SIGNAL MODEL {AppVersion.Current}";
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(280)
        };
        _previewTimer.Tick += PreviewTimer_Tick;
        _fontPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _fontPreviewTimer.Tick += FontPreviewTimer_Tick;
        _colorPreviewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // Colour is a pair of shader constants, not a glyph-atlas rebuild.
            // One update per display frame keeps dragging fluid without
            // flooding the render thread with every raw mouse event.
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _colorPreviewTimer.Tick += ColorPreviewTimer_Tick;
        _virtualResolutionTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(320)
        };
        _virtualResolutionTimer.Tick +=
            VirtualResolutionTimer_Tick;
        _sectionNavigationTimer = new DispatcherTimer(
            DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _sectionNavigationTimer.Tick += SectionNavigationTimer_Tick;
        AddHandler(
            TextBox.TextChangedEvent,
            new TextChangedEventHandler(AnyTextBox_TextChanged));
        AddHandler(
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(AnySelection_Changed));
        AddHandler(
            ToggleButton.CheckedEvent,
            new RoutedEventHandler(AnyToggle_Changed));
        AddHandler(
            ToggleButton.UncheckedEvent,
            new RoutedEventHandler(AnyToggle_Changed));
        PopulateFontFamilies();
        SelectByTag(CurveKindCombo, FlowCurveProfiles.TerminalKind);
        SelectByTag(ImagePreparationModeCombo, "Custom");
        _activePlaylistId = _playlists[0].Id;
        RefreshPlaylistUi();
        RefreshCurveEditor();
        UpdateImagePreparationUi();
        UpdateCollapsibleSections();
        RefreshLabels(force: true);
        _loading = false;
        Loaded += (_, _) => UpdateSectionNavigationSelection();
    }

    public void LoadSettings(AppSettings settings)
    {
        LoadSettingsCore(settings, preserveAppliedSettings: false);
        _playlistFileVersion = _playlistStore.FileVersion();
    }

    public void SetPlaylistImageAvailability(
        string path,
        bool available)
    {
        foreach (ImagePlaylistEntry entry in _playlists
                     .SelectMany(playlist => playlist.Entries)
                     .Where(entry => string.Equals(
                         entry.Path,
                         path,
                         StringComparison.OrdinalIgnoreCase)))
        {
            entry.SetAvailability(available);
        }
    }

    private void LoadSettingsCore(
        AppSettings settings,
        bool preserveAppliedSettings)
    {
        AppSettings container = settings.Copy();
        _monitors = OutputDeviceCatalog.Capture(container);
        MonitorTopology.EnsureProfiles(container, _monitors);
        if (!preserveAppliedSettings)
            _source = container.Copy();
        _draftSettings = container.Copy();
        EnsureSelectedMonitor();
        AppSettings displaySettings = SelectedMonitorSettings(_draftSettings);

        _livePreviewFontSize = displaySettings.FontSize;
        _livePreviewGlyphStretch = displaySettings.GlyphStretch;
        _livePreviewGlyphWeight = displaySettings.GlyphWeight;
        _speedMinValue = displaySettings.SpeedMin;
        _speedMaxValue = displaySettings.SpeedMax;
        _memoryMinValue = displaySettings.MemoryDurationMin;
        _memoryMaxValue = displaySettings.MemoryDurationMax;
        _imageDurationSecondsValue = displaySettings.ImageDurationSeconds;
        _attackIdleMinutesValue = container.AttackIdleMinutes;
        _attackTransitionSecondsValue =
            container.AttackTransitionSeconds;
        _loading = true;
        VirtualOutputWidthSlider.Value =
            container.VirtualOutputWidth;
        VirtualOutputHeightSlider.Value =
            container.VirtualOutputHeight;
        RefreshMonitorTopologyUi();
        SpeedMinSlider.Value = Math.Clamp(
            displaySettings.SpeedMin,
            SpeedMinSlider.Minimum,
            SpeedMinSlider.Maximum);
        SpeedMaxSlider.Value = Math.Clamp(
            displaySettings.SpeedMax,
            SpeedMaxSlider.Minimum,
            SpeedMaxSlider.Maximum);
        DensitySlider.Value = displaySettings.Density;
        TrailMinSlider.Value = displaySettings.TrailLengthMin;
        TrailMaxSlider.Value = displaySettings.TrailLengthMax;
        MemoryMinSlider.Value = Math.Clamp(
            displaySettings.MemoryDurationMin,
            MemoryMinSlider.Minimum,
            MemoryMinSlider.Maximum);
        MemoryMaxSlider.Value = Math.Clamp(
            displaySettings.MemoryDurationMax,
            MemoryMaxSlider.Minimum,
            MemoryMaxSlider.Maximum);
        SignalMinSlider.Value = displaySettings.SignalStrengthMin;
        SignalMaxSlider.Value = displaySettings.SignalStrengthMax;
        SignalGlowKeysSlider.Value = displaySettings.SignalGlowKeys;
        SignalGlowPrioritySlider.Value = displaySettings.SignalGlowPriority;
        HeadBrightnessSlider.Value = displaySettings.HeadBrightness;
        HeadGlowSlider.Value = displaySettings.HeadGlow;
        HeadImpulseDecaySlider.Value = displaySettings.HeadImpulseDecay;
        HeadImpulseProbabilitySlider.Value = displaySettings.HeadImpulseProbability;
        HeadWeightSlider.Value = displaySettings.HeadWeight;
        InterceptionSlider.Value = displaySettings.InterceptionRate;
        StreamLifetimeMinSlider.Value = displaySettings.StreamLifetimeMin;
        StreamLifetimeMaxSlider.Value = displaySettings.StreamLifetimeMax;
        FontSizeSlider.Value = displaySettings.FontSize;
        GlyphStretchSlider.Value = displaySettings.GlyphStretch;
        GlyphWeightSlider.Value = displaySettings.GlyphWeight;
        SignalHueSlider.Value = displaySettings.SignalHue;
        SignalBrightnessSlider.Value = displaySettings.SignalBrightness;
        BackgroundHueSlider.Value = displaySettings.BackgroundHue;
        BackgroundBrightnessSlider.Value = displaySettings.BackgroundBrightness;
        DurationSlider.Value = Math.Clamp(
            displaySettings.ImageDurationSeconds,
            DurationSlider.Minimum,
            DurationSlider.Maximum);
        ImageExpressivenessSlider.Value = displaySettings.ImageExpressiveness;
        ImageGlyphMatchSlider.Value = displaySettings.ImageGlyphMatch;
        ImageStabilitySlider.Value = displaySettings.ImageStability;
        ImageResistanceSlider.Value = displaySettings.ImageResistance;
        ImageBrightnessSlider.Value = displaySettings.ImageBrightness;
        ImageLocalContrastSlider.Value = displaySettings.ImageLocalContrast;
        ImageDetailStrengthSlider.Value = displaySettings.ImageDetailStrength;
        ImageEdgeStrengthSlider.Value = displaySettings.ImageEdgeStrength;
        ImageShadowBalanceSlider.Value = displaySettings.ImageShadowBalance;
        ImagePaletteAdaptationSlider.Value = displaySettings.ImagePaletteAdaptation;
        ImageToneCalmnessSlider.Value = displaySettings.ImageToneCalmness;
        _lengthCurve = FlowCurveMath.Normalize(displaySettings.TrailLengthCurve, increasing: true);
        _speedCurve = FlowCurveMath.Normalize(displaySettings.SpeedCurve, increasing: true);
        _signalCurve = FlowCurveMath.Normalize(displaySettings.SignalCurve, increasing: true);
        _filterCurve = FlowCurveMath.Normalize(displaySettings.StreamFilterCurve, increasing: true);
        _memoryCurve = FlowCurveMath.Normalize(displaySettings.MemoryCurve, increasing: true);
        _curveAdjustments[FlowCurveProfiles.SpeedKind] =
            displaySettings.SpeedCurveAdjustment.Copy();
        _curveAdjustments[FlowCurveProfiles.LengthKind] =
            displaySettings.TrailLengthCurveAdjustment.Copy();
        _curveAdjustments[FlowCurveProfiles.SignalKind] =
            displaySettings.SignalCurveAdjustment.Copy();
        _curveAdjustments[FlowCurveProfiles.FilterKind] =
            displaySettings.StreamFilterCurveAdjustment.Copy();
        _curveAdjustments[FlowCurveProfiles.MemoryKind] =
            displaySettings.MemoryCurveAdjustment.Copy();
        ImageModeCheck.IsChecked = displaySettings.ImageMode;
        _playlists = _draftSettings.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        _activePlaylistId = displaySettings.ActiveImagePlaylistId;
        RefreshPlaylistUi();
        RefreshPresetCatalog(container.ActivePresetId);
        _source.ActivePresetId = _selectedPresetId;
        AutostartCheck.IsChecked = container.StartWithWindows;
        PauseDuringFullscreenAppsCheck.IsChecked = container.PauseDuringFullscreenApps;
        AttackSystemEnabledCheck.IsChecked =
            container.AttackSystemEnabled;
        AttackIdleMinutesSlider.Value = Math.Clamp(
            container.AttackIdleMinutes,
            AttackIdleMinutesSlider.Minimum,
            AttackIdleMinutesSlider.Maximum);
        AttackTransitionSecondsSlider.Value = Math.Clamp(
            container.AttackTransitionSeconds,
            AttackTransitionSecondsSlider.Minimum,
            AttackTransitionSecondsSlider.Maximum);
        UpdateCollapsibleSections();
        EnsureFontOption(displaySettings.FontFamily);
        SelectByTag(FontCombo, displaySettings.FontFamily);
        SelectByTag(ImagePreparationModeCombo, displaySettings.ImagePreparationMode);
        SelectByTag(ImageStructureModeCombo, displaySettings.ImageStructureMode);
        SelectByTag(FpsCombo, container.FramesPerSecond.ToString());
        if (CurveKindCombo.SelectedIndex < 0)
            SelectByTag(CurveKindCombo, FlowCurveProfiles.TerminalKind);
        RefreshCurveEditor();
        UpdateImagePreparationUi();
        UpdateMonitorRouteNotices();
        _hasPendingChanges = preserveAppliedSettings
            && !AppSettingsComparer.Equivalent(_source, _draftSettings);
        _previewTimer.Stop();
        _fontPreviewTimer.Stop();
        _colorPreviewTimer.Stop();
        _virtualResolutionTimer.Stop();
        RefreshLabels(force: true);
        _loading = false;
        SetSynchronizedStatus();
        UpdateFooterButtons();
    }

    public void ForceClose()
    {
        _previewTimer.Stop();
        _fontPreviewTimer.Stop();
        _colorPreviewTimer.Stop();
        _virtualResolutionTimer.Stop();
        _sectionNavigationTimer.Stop();
        _allowClose = true;
        Close();
    }

    public void SetPauseState(bool paused)
    {
        _wallpaperPaused = paused;
        if (PauseWallpaperButton is null)
            return;
        PauseWallpaperButton.Content = paused ? "▶" : "■";
        PauseWallpaperButton.ToolTip = paused
            ? "Возобновить вывод кода"
            : "Остановить вывод и показать обычные обои Windows";
    }

    public void SetVirtualOutputState(
        string monitorId,
        bool open)
    {
        if (open)
            _openOutputMonitorIds.Add(monitorId);
        else
            _openOutputMonitorIds.Remove(monitorId);
        RefreshMonitorVisuals();
    }

    public void SetVirtualOutputStates(
        IEnumerable<string> monitorIds)
    {
        _openOutputMonitorIds.Clear();
        _openOutputMonitorIds.UnionWith(monitorIds);
        RefreshMonitorVisuals();
    }

    public void SetRuntimeStatus(string status, bool isError, string diagnosticLogPath)
    {
        _runtimeStatus = status;
        _diagnosticLogPath = diagnosticLogPath;
        if (RuntimeStatusTitle is null
            || RuntimeStatusText is null)
            return;
        RuntimeStatusText.Text = status;
        RuntimeStatusTitle.Foreground = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(255, 177, 102)
                : System.Windows.Media.Color.FromRgb(121, 168, 136));
        RuntimeStatusText.Foreground = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(255, 213, 174)
                : System.Windows.Media.Color.FromRgb(131, 255, 170));
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        string report = DiagnosticLog.BuildReport(_runtimeStatus);
        try
        {
            if (File.Exists(_diagnosticLogPath))
            {
                string log = File.ReadAllText(_diagnosticLogPath);
                const int maximumLogCharacters = 16_000;
                if (log.Length > maximumLogCharacters)
                    log = log[^maximumLogCharacters..];
                report += Environment.NewLine + "Последние записи:" + Environment.NewLine + log;
            }
            System.Windows.Clipboard.SetText(report);
            StatusText.Text = "ДИАГНОСТИЧЕСКИЙ ОТЧЁТ СКОПИРОВАН";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"НЕ УДАЛОСЬ СКОПИРОВАТЬ ОТЧЁТ // {exception.Message}";
        }
    }

    private void OpenDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticLog.Write("Оператор открыл журнал диагностики.");
            Process.Start(new ProcessStartInfo(_diagnosticLogPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = $"НЕ УДАЛОСЬ ОТКРЫТЬ ЖУРНАЛ // {exception.Message}";
        }
    }

    private void OpenPowerSettingsLink_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenSystemSettings(
            new ProcessStartInfo("ms-settings:powersleep")
            {
                UseShellExecute = true
            },
            "ОТКРЫТЫ ПАРАМЕТРЫ ОТКЛЮЧЕНИЯ ЭКРАНА");

    private void OpenScreenSaverSettingsLink_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenSystemSettings(
            new ProcessStartInfo("control.exe")
            {
                Arguments = "desk.cpl,,@screensaver",
                UseShellExecute = true
            },
            "ОТКРЫТЫ ПАРАМЕТРЫ СИСТЕМНОЙ ЗАСТАВКИ");

    private void OpenSystemSettings(
        ProcessStartInfo startInfo,
        string successStatus)
    {
        try
        {
            Process.Start(startInfo);
            StatusText.Text = successStatus;
        }
        catch (Exception exception)
        {
            StatusText.Text =
                $"НЕ УДАЛОСЬ ОТКРЫТЬ ПАРАМЕТРЫ WINDOWS // {exception.Message}";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            DiscardPreviewAndHide();
        }
        base.OnClosing(e);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        _fontPreviewTimer.Stop();
        _colorPreviewTimer.Stop();
        _virtualResolutionTimer.Stop();
        UpdateDraftStatus();
        if (!_hasPendingChanges)
        {
            Hide();
            return;
        }

        _loading = true;
        CommitAllNumericInputs();
        _loading = false;
        AppSettings updated = ReadSettingsFromControls();
        if (!TrySaveActivePreset(updated))
            return;
        SettingsApplied?.Invoke(updated);
        _playlistFileVersion = _playlistStore.FileVersion();
        _source = updated.Copy();
        _draftSettings = updated.Copy();
        AppSettings appliedDisplay = SelectedMonitorSettings(_draftSettings);
        _livePreviewFontSize = appliedDisplay.FontSize;
        _livePreviewGlyphStretch = appliedDisplay.GlyphStretch;
        _livePreviewGlyphWeight = appliedDisplay.GlyphWeight;
        _hasPendingChanges = false;
        UpdateFooterButtons();
        StatusText.Text = updated.ImageMode && !HasAvailablePlaylistImages(updated)
            ? "ПАРАМЕТРЫ ПРИНЯТЫ // В ПЛЕЙЛИСТЕ НЕТ ДОСТУПНЫХ ОБРАЗОВ"
            : "ПАРАМЕТРЫ ПРИНЯТЫ // ПОТОК СИНХРОНИЗИРОВАН";
    }

    private AppSettings ReadSettingsFromControls()
    {
        AppSettings updated = _draftSettings.Copy();
        MonitorTopology.EnsureProfiles(updated, _monitors);
        AppSettings display = SelectedMonitorSettings(updated);
        display.SpeedMin = _speedMinValue;
        display.SpeedMax = _speedMaxValue;
        display.Density = DensitySlider.Value;
        display.TrailLengthMin = TrailMinSlider.Value;
        display.TrailLengthMax = TrailMaxSlider.Value;
        display.MemoryDurationMin = _memoryMinValue;
        display.MemoryDurationMax = _memoryMaxValue;
        display.SignalStrengthMin = SignalMinSlider.Value;
        display.SignalStrengthMax = SignalMaxSlider.Value;
        display.SignalGlowKeys = SignalGlowKeysSlider.Value;
        display.SignalGlowPriority = SignalGlowPrioritySlider.Value;
        display.HeadBrightness = HeadBrightnessSlider.Value;
        display.HeadGlow = HeadGlowSlider.Value;
        display.HeadImpulseDecay = HeadImpulseDecaySlider.Value;
        display.HeadImpulseProbability = HeadImpulseProbabilitySlider.Value;
        display.HeadWeight = HeadWeightSlider.Value;
        display.InterceptionRate = InterceptionSlider.Value;
        display.StreamLifetimeMin = StreamLifetimeMinSlider.Value;
        display.StreamLifetimeMax = StreamLifetimeMaxSlider.Value;
        display.SpeedCurve = _speedCurve.Select(point => point.Copy()).ToList();
        display.TrailLengthCurve = _lengthCurve.Select(point => point.Copy()).ToList();
        display.SignalCurve = _signalCurve.Select(point => point.Copy()).ToList();
        display.StreamFilterCurve = _filterCurve.Select(point => point.Copy()).ToList();
        display.MemoryCurve = _memoryCurve.Select(point => point.Copy()).ToList();
        display.SpeedCurveAdjustment =
            AdjustmentFor(FlowCurveProfiles.SpeedKind).Copy();
        display.TrailLengthCurveAdjustment =
            AdjustmentFor(FlowCurveProfiles.LengthKind).Copy();
        display.SignalCurveAdjustment =
            AdjustmentFor(FlowCurveProfiles.SignalKind).Copy();
        display.StreamFilterCurveAdjustment =
            AdjustmentFor(FlowCurveProfiles.FilterKind).Copy();
        display.MemoryCurveAdjustment =
            AdjustmentFor(FlowCurveProfiles.MemoryKind).Copy();
        display.FontSize = FontSizeSlider.Value;
        display.GlyphStretch = GlyphStretchSlider.Value;
        display.GlyphWeight = GlyphWeightSlider.Value;
        display.SignalHue = SignalHueSlider.Value;
        display.SignalBrightness = SignalBrightnessSlider.Value;
        display.BackgroundHue = BackgroundHueSlider.Value;
        display.BackgroundBrightness = BackgroundBrightnessSlider.Value;
        display.ImageDurationSeconds = _imageDurationSecondsValue;
        display.ImageExpressiveness = ImageExpressivenessSlider.Value;
        display.ImageGlyphMatch = ImageGlyphMatchSlider.Value;
        display.ImageStability = ImageStabilitySlider.Value;
        display.ImageResistance = ImageResistanceSlider.Value;
        display.ImageBrightness = ImageBrightnessSlider.Value;
        display.ImagePreparationMode = SelectedTag(ImagePreparationModeCombo, "Auto");
        display.ImageLocalContrast = ImageLocalContrastSlider.Value;
        display.ImageDetailStrength = ImageDetailStrengthSlider.Value;
        display.ImageEdgeStrength = ImageEdgeStrengthSlider.Value;
        display.ImageShadowBalance = ImageShadowBalanceSlider.Value;
        display.ImagePaletteAdaptation = ImagePaletteAdaptationSlider.Value;
        display.ImageToneCalmness = ImageToneCalmnessSlider.Value;
        display.ImageStructureMode = SelectedTag(ImageStructureModeCombo, "Tonal");
        display.ImageMode = ImageModeCheck.IsChecked == true;
        updated.ImagePlaylists = _playlists
            .Select(playlist => playlist.Copy())
            .ToList();
        display.ActiveImagePlaylistId = _activePlaylistId;
        updated.StartWithWindows = AutostartCheck.IsChecked == true;
        updated.PauseDuringFullscreenApps = PauseDuringFullscreenAppsCheck.IsChecked == true;
        updated.AttackSystemEnabled =
            AttackSystemEnabledCheck.IsChecked == true;
        updated.AttackIdleMinutes = _attackIdleMinutesValue;
        updated.AttackTransitionSeconds =
            _attackTransitionSecondsValue;
        updated.VirtualOutputWidth =
            (int)Math.Round(VirtualOutputWidthSlider.Value);
        updated.VirtualOutputHeight =
            (int)Math.Round(VirtualOutputHeightSlider.Value);
        MonitorProfile? virtualProfile = MonitorTopology.Find(
            updated.MonitorProfiles,
            OutputDeviceCatalog.VirtualMonitorId);
        updated.VirtualMonitorEnabled =
            virtualProfile is not null
            && virtualProfile.FlowMode != MonitorLinkMode.Disabled;
        updated.ActivePresetId = _selectedPresetId;
        display.FontFamily = SelectedTag(FontCombo, "MS Gothic");
        updated.FramesPerSecond = int.TryParse(SelectedTag(FpsCombo, "24"), out int fps) ? fps : 24;
        display.Normalize(includeMonitorProfiles: false);
        SynchronizeLegacySettings(updated);
        updated.Normalize();
        return updated;
    }

    private void EnsureSelectedMonitor()
    {
        if (_monitors.Count == 0)
        {
            _selectedMonitorId = "";
            return;
        }
        if (_monitors.Any(monitor => string.Equals(
                monitor.Id,
                _selectedMonitorId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _selectedMonitorId = _monitors
            .FirstOrDefault(monitor => monitor.Primary)?.Id
            ?? _monitors[0].Id;
    }

    private MonitorProfile SelectedMonitorProfile(AppSettings settings)
    {
        MonitorTopology.EnsureProfiles(settings, _monitors);
        return MonitorTopology.Find(
                settings.MonitorProfiles,
                _selectedMonitorId)
            ?? settings.MonitorProfiles.First();
    }

    private AppSettings SelectedMonitorSettings(AppSettings settings) =>
        SelectedMonitorProfile(settings).Settings;

    private void RefreshMonitorTopologyUi()
    {
        if (MonitorTopologyCanvas is null
            || MonitorFlowModeCombo is null
            || MonitorDatabaseModeCombo is null)
        {
            return;
        }

        MonitorProfile selected = SelectedMonitorProfile(_draftSettings);
        List<MonitorRouteChoice> flowChoices =
            CreateRouteChoices(MonitorRouteDomain.Flow, selected.MonitorId);
        MonitorFlowModeCombo.ItemsSource = flowChoices;
        MonitorFlowModeCombo.SelectedItem = MatchRouteChoice(
            flowChoices,
            selected.FlowMode,
            selected.FlowSourceMonitorId);

        List<MonitorRouteChoice> databaseChoices =
            CreateRouteChoices(MonitorRouteDomain.Database, selected.MonitorId);
        MonitorDatabaseModeCombo.ItemsSource = databaseChoices;
        MonitorDatabaseModeCombo.SelectedItem = MatchRouteChoice(
            databaseChoices,
            selected.DatabaseMode,
            selected.DatabaseSourceMonitorId);
        MonitorDatabaseModeCombo.IsEnabled =
            !OutputDeviceCatalog.IsVirtual(selected.MonitorId)
            || selected.FlowMode != MonitorLinkMode.Disabled;
        RefreshMonitorVisuals();
        UpdateMonitorRouteNotices();
    }

    private void RefreshMonitorVisuals()
    {
        if (MonitorTopologyCanvas is null || _monitors.Count == 0)
            return;

        MonitorTopologyCanvas.Children.Clear();
        _virtualMonitorWidthCombo = null;
        _virtualMonitorHeightCombo = null;
        _virtualMonitorVisual = null;

        List<(MonitorDescriptor Monitor, double Width, double Height)>
            layout = _monitors
                .Select(monitor =>
                {
                    double minimumWidth = monitor.IsVirtual ? 190 : 150;
                    double minimumHeight = monitor.IsVirtual ? 125 : 115;
                    return (
                        monitor,
                        Math.Max(
                            minimumWidth,
                            monitor.Bounds.Width * TopologyDeviceScale),
                        Math.Max(
                            minimumHeight,
                            monitor.Bounds.Height * TopologyDeviceScale));
                })
                .ToList();
        double minimumLeft = layout.Min(item =>
            item.Monitor.Bounds.Left * TopologyDeviceScale);
        double minimumTop = layout.Min(item =>
            item.Monitor.Bounds.Top * TopologyDeviceScale);
        double maximumRight = layout.Max(item =>
            item.Monitor.Bounds.Left * TopologyDeviceScale
            + item.Width);
        double maximumBottom = layout.Max(item =>
            item.Monitor.Bounds.Top * TopologyDeviceScale
            + item.Height);
        _topologyOriginX =
            (minimumLeft - TopologyPadding) / TopologyDeviceScale;
        _topologyOriginY =
            (minimumTop - TopologyPadding) / TopologyDeviceScale;
        MonitorTopologyCanvas.Width = Math.Max(
            320,
            maximumRight - minimumLeft + TopologyPadding * 2);
        MonitorTopologyCanvas.Height = Math.Max(
            220,
            maximumBottom - minimumTop + TopologyPadding * 2);

        int physicalIndex = 0;
        foreach ((
                     MonitorDescriptor monitor,
                     double width,
                     double height) in layout)
        {
            MonitorProfile? profile = MonitorTopology.Find(
                _draftSettings.MonitorProfiles,
                monitor.Id);
            if (profile is null)
                continue;

            int number = monitor.IsVirtual
                ? 0
                : MonitorNumber(monitor, physicalIndex++);
            double left =
                (monitor.Bounds.Left - _topologyOriginX)
                * TopologyDeviceScale;
            double top =
                (monitor.Bounds.Top - _topologyOriginY)
                * TopologyDeviceScale;
            Border visual = CreateMonitorVisual(
                monitor,
                profile,
                number,
                width,
                height);
            visual.Width = width;
            visual.Height = height;
            Canvas.SetLeft(visual, left);
            Canvas.SetTop(visual, top);
            MonitorTopologyCanvas.Children.Add(visual);
            if (monitor.IsVirtual)
                _virtualMonitorVisual = visual;
        }

        MonitorTopologyScaleTransform.ScaleX = _topologyZoom;
        MonitorTopologyScaleTransform.ScaleY = _topologyZoom;
        if (_topologyAutoFit)
            QueueMonitorTopologyFit();
    }

    private Border CreateMonitorVisual(
        MonitorDescriptor monitor,
        MonitorProfile profile,
        int number,
        double width,
        double height)
    {
        bool selected = string.Equals(
            monitor.Id,
            _selectedMonitorId,
            StringComparison.OrdinalIgnoreCase);
        double shortSide = Math.Min(width, height);
        double titleFontSize = Math.Clamp(shortSide * 0.13, 12, 17);
        double detailFontSize = Math.Clamp(shortSide * 0.095, 10, 13);
        double nameFontSize = Math.Clamp(shortSide * 0.085, 9, 12);
        double badgeSize = Math.Clamp(shortSide * 0.24, 20, 31);
        double contentWidth = Math.Max(36, width - 20);
        Grid screen = new();
        StackPanel identity = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(
                4,
                badgeSize + 8,
                4,
                18)
        };
        identity.Children.Add(new TextBlock
        {
            Text = monitor.IsVirtual
                ? "ВИРТУАЛЬНЫЙ"
                : $"ЭКРАН {number}",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
            FontSize = titleFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = BrushFromRgb(
                selected ? 0xD8 : 0x79,
                selected ? 0xFF : 0xA8,
                selected ? 0xE5 : 0x88)
        });
        if (monitor.IsVirtual)
        {
            double inputWidth = Math.Max(
                46,
                (width - 32) * 0.5);
            StackPanel resolution = new()
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            _virtualMonitorWidthCombo =
                CreateVirtualResolutionCombo(
                    true,
                    monitor.Bounds.Width,
                    inputWidth);
            _virtualMonitorHeightCombo =
                CreateVirtualResolutionCombo(
                    false,
                    monitor.Bounds.Height,
                    inputWidth);
            resolution.Children.Add(
                _virtualMonitorWidthCombo);
            resolution.Children.Add(new TextBlock
            {
                Text = "×",
                Width = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = BrushFromRgb(0x83, 0xFF, 0xAA)
            });
            resolution.Children.Add(
                _virtualMonitorHeightCombo);
            identity.Children.Add(resolution);
        }
        else
        {
            identity.Children.Add(new TextBlock
            {
                Text = $"{monitor.Bounds.Width}×{monitor.Bounds.Height}",
                Visibility = height >= 68
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
                FontSize = detailFontSize,
                Foreground = BrushFromRgb(0x83, 0xFF, 0xAA)
            });
            identity.Children.Add(new TextBlock
            {
                Text = monitor.FriendlyName,
                Visibility = height >= 88 && width >= 90
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                Width = contentWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Consolas"),
                FontSize = nameFontSize,
                Foreground = BrushFromRgb(0x55, 0xB9, 0x78)
            });
        }
        screen.Children.Add(identity);

        if (monitor.IsVirtual)
        {
            Border dragHandle = new()
            {
                Tag = monitor.Id,
                Width = 34,
                Height = 19,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Background = BrushFromRgb(0x05, 0x24, 0x16),
                BorderBrush = BrushFromRgb(0x16, 0x71, 0x3D),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = WpfCursors.SizeAll,
                ToolTip = "Переместить виртуальный монитор",
                Child = new TextBlock
                {
                    Text = "⠿",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 13,
                    Foreground = BrushFromRgb(0x62, 0xFF, 0x8F)
                }
            };
            dragHandle.PreviewMouseLeftButtonDown +=
                VirtualMonitorDragHandle_MouseLeftButtonDown;
            dragHandle.PreviewMouseMove +=
                VirtualMonitorDragHandle_MouseMove;
            dragHandle.PreviewMouseLeftButtonUp +=
                VirtualMonitorDragHandle_MouseLeftButtonUp;
            dragHandle.LostMouseCapture +=
                VirtualMonitorDragHandle_LostMouseCapture;
            screen.Children.Add(dragHandle);
        }

        bool flowDisabled =
            profile.FlowMode == MonitorLinkMode.Disabled;
        System.Windows.Controls.Button openWindow = new()
        {
            Tag = monitor.Id,
            Content = _openOutputMonitorIds.Contains(monitor.Id)
                ? "\uE711"
                : "\uE8A7",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = Math.Clamp(shortSide * 0.27, 24, 32),
            Height = Math.Clamp(shortSide * 0.23, 22, 29),
            Padding = new Thickness(3, 1, 3, 1),
            Margin = new Thickness(0, 0, 3, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Style = TryFindResource("QuickIconButton") as Style,
            IsEnabled = !flowDisabled,
            ToolTip = _openOutputMonitorIds.Contains(monitor.Id)
                    ? "Закрыть отдельное окно этого потока"
                    : "Открыть копию этого потока в отдельном окне"
        };
        openWindow.Click += MonitorOutputWindowButton_Click;
        screen.Children.Add(openWindow);

        screen.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = profile.FlowMode == MonitorLinkMode.Disabled
                ? BrushFromRgb(0xFF, 0x45, 0x45)
                : BrushFromRgb(0x00, 0xE6, 0x67),
            Effect = null
        });

        if (profile.FlowMode == MonitorLinkMode.Relay)
        {
            AddMonitorBadge(
                screen,
                MonitorBadgeKind.FlowRelay,
                SourceMonitorMarker(profile.FlowSourceMonitorId),
                HorizontalAlignment.Left,
                VerticalAlignment.Top,
                badgeSize);
        }
        else if (profile.FlowMode == MonitorLinkMode.Extend)
        {
            AddMonitorBadge(
                screen,
                MonitorBadgeKind.FlowExtend,
                SourceMonitorMarker(profile.FlowSourceMonitorId),
                HorizontalAlignment.Left,
                VerticalAlignment.Top,
                badgeSize);
        }

        if (profile.DatabaseMode == MonitorLinkMode.Isolated)
        {
            AddMonitorBadge(
                screen,
                MonitorBadgeKind.DatabaseIsolated,
                null,
                HorizontalAlignment.Right,
                VerticalAlignment.Top,
                badgeSize);
        }
        else if (profile.DatabaseMode == MonitorLinkMode.Relay)
        {
            AddMonitorBadge(
                screen,
                MonitorBadgeKind.DatabaseRelay,
                SourceMonitorMarker(profile.DatabaseSourceMonitorId),
                HorizontalAlignment.Right,
                VerticalAlignment.Top,
                badgeSize);
        }
        else if (profile.DatabaseMode == MonitorLinkMode.Extend)
        {
            AddMonitorBadge(
                screen,
                MonitorBadgeKind.DatabaseExtend,
                SourceMonitorMarker(profile.DatabaseSourceMonitorId),
                HorizontalAlignment.Right,
                VerticalAlignment.Top,
                badgeSize);
        }

        Border border = new()
        {
            Tag = monitor.Id,
            Background = monitor.IsVirtual
                ? BrushFromRgb(
                    selected ? 0x08 : 0x03,
                    selected ? 0x2B : 0x12,
                    selected ? 0x29 : 0x13)
                : BrushFromRgb(
                    selected ? 0x09 : 0x02,
                    selected ? 0x27 : 0x08,
                    selected ? 0x19 : 0x06),
            BorderBrush = BrushFromRgb(
                selected ? 0x00 : 0x16,
                selected ? 0xE6 : 0x4B,
                selected ? 0x67 : 0x2C),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5),
            Opacity = monitor.IsVirtual && flowDisabled ? 0.54 : 1.0,
            Child = screen,
            Cursor = WpfCursors.Hand,
            ToolTip = $"{monitor.FriendlyName}\n"
                + $"{monitor.Bounds.Width}×{monitor.Bounds.Height} "
                + $"@ ({monitor.Bounds.Left}, {monitor.Bounds.Top})\n"
                + $"Поток: {RouteSummary(profile.FlowMode, profile.FlowSourceMonitorId)}\n"
                + $"База: {RouteSummary(profile.DatabaseMode, profile.DatabaseSourceMonitorId)}"
        };
        border.ContextMenu = CreateMonitorContextMenu(
            monitor,
            profile);
        border.MouseLeftButtonDown += MonitorVisual_MouseLeftButtonDown;
        return border;
    }

    private ContextMenu CreateMonitorContextMenu(
        MonitorDescriptor monitor,
        MonitorProfile profile)
    {
        ContextMenu context = new()
        {
            Style = TryFindResource("MonitorContextMenu") as Style,
            Background = BrushFromRgb(0x02, 0x08, 0x06),
            BorderBrush = BrushFromRgb(0x23, 0x8A, 0x4B),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        Grid columns = new()
        {
            Margin = new Thickness(3)
        };
        columns.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        columns.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(224)
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1)
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(224)
        });

        Border title = new()
        {
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(10, 8, 10, 8),
            Background = BrushFromRgb(0x06, 0x1A, 0x0F),
            BorderBrush = BrushFromRgb(0x16, 0x4B, 0x2C),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock
            {
                Text = monitor.Label.ToUpperInvariant(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushFromRgb(0x9D, 0xFF, 0xBD),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetColumnSpan(title, 3);
        columns.Children.Add(title);

        FrameworkElement flowColumn = CreateMonitorRouteColumn(
            context,
            monitor,
            profile,
            MonitorRouteDomain.Flow);
        Grid.SetRow(flowColumn, 1);
        columns.Children.Add(flowColumn);
        Border separator = new()
        {
            Background = BrushFromRgb(0x16, 0x4B, 0x2C),
            Margin = new Thickness(7, 0, 7, 0)
        };
        Grid.SetRow(separator, 1);
        Grid.SetColumn(separator, 1);
        columns.Children.Add(separator);
        FrameworkElement databaseColumn = CreateMonitorRouteColumn(
            context,
            monitor,
            profile,
            MonitorRouteDomain.Database);
        Grid.SetRow(databaseColumn, 1);
        Grid.SetColumn(databaseColumn, 2);
        columns.Children.Add(databaseColumn);

        context.Items.Add(new MenuItem
        {
            Header = columns,
            Style = TryFindResource("MonitorContextHostItem") as Style,
            StaysOpenOnClick = true
        });
        return context;
    }

    private FrameworkElement CreateMonitorRouteColumn(
        ContextMenu context,
        MonitorDescriptor monitor,
        MonitorProfile profile,
        MonitorRouteDomain domain)
    {
        StackPanel column = new();
        column.Children.Add(new TextBlock
        {
            Text = domain == MonitorRouteDomain.Flow
                ? "ПОТОК ДАННЫХ"
                : "БАЗА ДАННЫХ",
            Margin = new Thickness(8, 5, 8, 7),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = domain == MonitorRouteDomain.Flow
                ? BrushFromRgb(0x62, 0xFF, 0x8F)
                : BrushFromRgb(0x7D, 0xE7, 0xFF)
        });

        MonitorLinkMode currentMode = domain == MonitorRouteDomain.Flow
            ? profile.FlowMode
            : profile.DatabaseMode;
        string currentSource = domain == MonitorRouteDomain.Flow
            ? profile.FlowSourceMonitorId
            : profile.DatabaseSourceMonitorId;
        foreach (MonitorRouteChoice choice in CreateRouteChoices(
                     domain,
                     monitor.Id))
        {
            bool current = choice.Mode == currentMode
                && (choice.Mode
                        is MonitorLinkMode.Isolated
                        or MonitorLinkMode.Disabled
                    || string.Equals(
                        choice.SourceMonitorId,
                        currentSource,
                        StringComparison.OrdinalIgnoreCase));
            System.Windows.Controls.Button route = new()
            {
                Content = (current ? "● " : "  ") + choice.Label,
                Style = TryFindResource(
                    "MonitorContextRouteButton") as Style,
                Foreground = current
                    ? BrushFromRgb(0x02, 0x08, 0x06)
                    : BrushFromRgb(0xD8, 0xFF, 0xE5),
                Background = current
                    ? BrushFromRgb(0x00, 0xC9, 0x5B)
                    : BrushFromRgb(0x06, 0x1A, 0x0F),
                BorderBrush = current
                    ? BrushFromRgb(0x83, 0xFF, 0xAA)
                    : BrushFromRgb(0x16, 0x4B, 0x2C),
                ToolTip = current
                    ? "Текущий режим"
                    : $"Применить к устройству «{monitor.Label}»"
            };
            route.Click += (_, e) =>
            {
                e.Handled = true;
                context.IsOpen = false;
                ApplyMonitorRouteSelection(
                    domain,
                    choice,
                    monitor.Id);
            };
            column.Children.Add(route);
        }
        return column;
    }

    private ComboBox CreateVirtualResolutionCombo(
        bool widthInput,
        int value,
        double width)
    {
        ComboBox input = new()
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            StaysOpenOnEdit = true,
            Width = width,
            Height = 23,
            Padding = new Thickness(2, 0, 2, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            ToolTip = widthInput
                ? "Ширина виртуального монитора, px"
                : "Высота виртуального монитора, px"
        };
        int[] values = widthInput
            ? [1280, 1920, 2560, 3440, 3840, 5120, 7680]
            : [720, 1080, 1440, 1600, 2160, 2880, 4320];
        foreach (int option in values)
        {
            input.Items.Add(new ComboBoxItem
            {
                Content = option.ToString(
                    CultureInfo.InvariantCulture)
            });
        }
        input.Text = value.ToString(
            CultureInfo.InvariantCulture);
        input.DropDownClosed +=
            VirtualResolutionCombo_DropDownClosed;
        input.LostKeyboardFocus +=
            VirtualResolutionCombo_LostKeyboardFocus;
        input.PreviewKeyDown +=
            VirtualResolutionCombo_PreviewKeyDown;
        input.PreviewMouseWheel +=
            VirtualResolutionCombo_PreviewMouseWheel;
        return input;
    }

    private static void AddMonitorBadge(
        Grid screen,
        MonitorBadgeKind kind,
        string? sourceMarker,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        double size)
    {
        SolidColorBrush foreground = kind is MonitorBadgeKind.FlowRelay
            or MonitorBadgeKind.FlowExtend
            ? BrushFromRgb(0x62, 0xFF, 0x8F)
            : BrushFromRgb(0x7D, 0xE7, 0xFF);
        Grid glyph = new()
        {
            Width = size + (sourceMarker is null ? 4 : 10),
            Height = size + 4
        };
        glyph.Children.Add(new Viewbox
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(MonitorBadgeGeometry(kind)),
                Stroke = foreground,
                StrokeThickness = 1.75,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent
            }
        });
        if (!string.IsNullOrWhiteSpace(sourceMarker))
        {
            glyph.Children.Add(new Border
            {
                MinWidth = Math.Max(13, size * 0.52),
                Height = Math.Max(13, size * 0.52),
                Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = BrushFromRgb(0x00, 0x0B, 0x07),
                BorderBrush = foreground,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    Text = sourceMarker,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = Math.Max(10, size * 0.43),
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground
                }
            });
        }

        screen.Children.Add(new Border
        {
            Margin = new Thickness(4, 3, 4, 3),
            Padding = new Thickness(3, 2, 3, 2),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Background = BrushFromRgb(0x01, 0x0E, 0x08),
            BorderBrush = BrushFromRgb(0x0A, 0x35, 0x20),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = glyph
        });
    }

    private static string MonitorBadgeGeometry(MonitorBadgeKind kind) =>
        kind switch
        {
            MonitorBadgeKind.FlowRelay =>
                "M 6,3 L 6,17 M 2.5,13.5 L 6,17 L 9.5,13.5 "
                + "M 17,3 L 17,17 M 13.5,13.5 L 17,17 L 20.5,13.5",
            MonitorBadgeKind.FlowExtend =>
                "M 6,3 L 6,11 C 6,13 8,14 10,14 L 18,14 "
                + "M 14.5,10.5 L 18,14 L 14.5,17.5 "
                + "M 12,4 L 18,4 L 18,9",
            MonitorBadgeKind.DatabaseRelay =>
                "M 2,6 C 2,3.8 10,3.8 10,6 C 10,8.2 2,8.2 2,6 "
                + "M 2,6 L 2,16 C 2,18.2 10,18.2 10,16 L 10,6 "
                + "M 2,11 C 2,13.2 10,13.2 10,11 "
                + "M 14,7 C 14,5.2 21,5.2 21,7 C 21,8.8 14,8.8 14,7 "
                + "M 14,7 L 14,17 C 14,18.8 21,18.8 21,17 L 21,7 "
                + "M 14,12 C 14,13.8 21,13.8 21,12",
            MonitorBadgeKind.DatabaseExtend =>
                "M 2,6 C 2,3.8 12,3.8 12,6 C 12,8.2 2,8.2 2,6 "
                + "M 2,6 L 2,17 C 2,19.2 12,19.2 12,17 L 12,6 "
                + "M 2,11.5 C 2,13.7 12,13.7 12,11.5 "
                + "M 12,12 L 20,12 M 16.5,8.5 L 20,12 L 16.5,15.5",
            _ =>
                "M 4,6 C 4,3.8 20,3.8 20,6 C 20,8.2 4,8.2 4,6 "
                + "M 4,6 L 4,17 C 4,19.2 20,19.2 20,17 L 20,6 "
                + "M 4,11.5 C 4,13.7 20,13.7 20,11.5"
        };

    private string RouteSummary(
        MonitorLinkMode mode,
        string sourceMonitorId)
    {
        string source = SourceMonitorMarker(sourceMonitorId);
        return mode switch
        {
            MonitorLinkMode.Relay => $"ретрансляция с устройства {source}",
            MonitorLinkMode.Extend => $"расширение устройства {source}",
            MonitorLinkMode.Disabled => "отключено",
            _ => "изолировано"
        };
    }

    private string SourceMonitorMarker(string monitorId)
    {
        for (int index = 0; index < _monitors.Count; index++)
        {
            if (string.Equals(
                _monitors[index].Id,
                monitorId,
                StringComparison.OrdinalIgnoreCase))
            {
                return _monitors[index].IsVirtual
                    ? "V"
                    : MonitorNumber(_monitors[index], index)
                        .ToString(CultureInfo.InvariantCulture);
            }
        }
        return "?";
    }

    private static int MonitorNumber(
        MonitorDescriptor monitor,
        int fallbackIndex) =>
        monitor.DisplayNumber > 0
            ? monitor.DisplayNumber
            : fallbackIndex + 1;

    private static SolidColorBrush BrushFromRgb(
        int red,
        int green,
        int blue) =>
        new(System.Windows.Media.Color.FromRgb(
            (byte)red,
            (byte)green,
            (byte)blue));

    private void MonitorTopologyScrollViewer_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (!_loading && _topologyAutoFit)
            QueueMonitorTopologyFit();
    }

    private void MonitorTopologyScrollViewer_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (_loading || _virtualMonitorDragging)
            return;
        if (e.OriginalSource is DependencyObject origin
            && IsFocusedVirtualResolutionInput(origin))
        {
            return;
        }

        System.Windows.Point pointer =
            e.GetPosition(MonitorTopologyScrollViewer);
        double previous = _topologyZoom;
        double factor = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
        double next = Math.Clamp(
            previous * factor,
            MinimumTopologyZoom,
            3.0);
        if (Math.Abs(next - previous) < 0.0001)
            return;

        double logicalX =
            (MonitorTopologyScrollViewer.HorizontalOffset + pointer.X)
            / previous;
        double logicalY =
            (MonitorTopologyScrollViewer.VerticalOffset + pointer.Y)
            / previous;
        _topologyAutoFit = false;
        SetMonitorTopologyZoom(next);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                MonitorTopologyScrollViewer.UpdateLayout();
                MonitorTopologyScrollViewer.ScrollToHorizontalOffset(
                    logicalX * next - pointer.X);
                MonitorTopologyScrollViewer.ScrollToVerticalOffset(
                    logicalY * next - pointer.Y);
            });
        e.Handled = true;
    }

    private void MonitorTopologyScrollViewer_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle))
            return;

        _topologyPanCandidate = true;
        _topologyPanButton = e.ChangedButton;
        _topologyPanStart =
            e.GetPosition(MonitorTopologyScrollViewer);
        _topologyPanHorizontalOffset =
            MonitorTopologyScrollViewer.HorizontalOffset;
        _topologyPanVerticalOffset =
            MonitorTopologyScrollViewer.VerticalOffset;
        if (e.ChangedButton == MouseButton.Middle)
        {
            BeginMonitorTopologyPan();
            e.Handled = true;
        }
    }

    private void MonitorTopologyScrollViewer_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_topologyPanCandidate && !_topologyPanning)
        {
            bool buttonPressed = _topologyPanButton switch
            {
                MouseButton.Right =>
                    e.RightButton == MouseButtonState.Pressed,
                MouseButton.Middle =>
                    e.MiddleButton == MouseButtonState.Pressed,
                _ => false
            };
            if (!buttonPressed)
            {
                _topologyPanCandidate = false;
                return;
            }

            System.Windows.Point candidatePosition =
                e.GetPosition(MonitorTopologyScrollViewer);
            double horizontalDistance = Math.Abs(
                candidatePosition.X - _topologyPanStart.X);
            double verticalDistance = Math.Abs(
                candidatePosition.Y - _topologyPanStart.Y);
            if (horizontalDistance
                    < SystemParameters.MinimumHorizontalDragDistance
                && verticalDistance
                    < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            BeginMonitorTopologyPan();
        }

        if (!_topologyPanning)
            return;
        if (e.RightButton != MouseButtonState.Pressed
            && e.MiddleButton != MouseButtonState.Pressed)
        {
            EndMonitorTopologyPan();
            return;
        }

        System.Windows.Point current =
            e.GetPosition(MonitorTopologyScrollViewer);
        MonitorTopologyScrollViewer.ScrollToHorizontalOffset(
            _topologyPanHorizontalOffset
            - (current.X - _topologyPanStart.X));
        MonitorTopologyScrollViewer.ScrollToVerticalOffset(
            _topologyPanVerticalOffset
            - (current.Y - _topologyPanStart.Y));
        e.Handled = true;
    }

    private void MonitorTopologyScrollViewer_PreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton
            is not (MouseButton.Right or MouseButton.Middle))
        {
            return;
        }
        if (_topologyPanCandidate && !_topologyPanning)
        {
            _topologyPanCandidate = false;
            return;
        }
        if (!_topologyPanning)
            return;
        EndMonitorTopologyPan();
        e.Handled = true;
    }

    private void MonitorTopologyScrollViewer_MouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_topologyPanCandidate
            && e.RightButton != MouseButtonState.Pressed
            && e.MiddleButton != MouseButtonState.Pressed)
        {
            _topologyPanCandidate = false;
        }
        if (_topologyPanning
            && e.RightButton != MouseButtonState.Pressed
            && e.MiddleButton != MouseButtonState.Pressed)
        {
            EndMonitorTopologyPan();
        }
    }

    private void EndMonitorTopologyPan()
    {
        _topologyPanCandidate = false;
        _topologyPanning = false;
        MonitorTopologyScrollViewer.ReleaseMouseCapture();
        MonitorTopologyScrollViewer.Cursor = WpfCursors.Arrow;
    }

    private void BeginMonitorTopologyPan()
    {
        _topologyPanCandidate = false;
        _topologyPanning = true;
        _topologyAutoFit = false;
        MonitorTopologyScrollViewer.Cursor = WpfCursors.ScrollAll;
        MonitorTopologyScrollViewer.CaptureMouse();
    }

    private void FitMonitorTopologyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _topologyAutoFit = true;
        QueueMonitorTopologyFit();
    }

    private void QueueMonitorTopologyFit()
    {
        if (_topologyFitQueued)
            return;
        _topologyFitQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _topologyFitQueued = false;
                if (_topologyAutoFit)
                    FitMonitorTopology();
            });
    }

    private void FitMonitorTopology()
    {
        double viewportWidth =
            MonitorTopologyScrollViewer.ActualWidth;
        double viewportHeight =
            MonitorTopologyScrollViewer.ActualHeight;
        if (viewportWidth < 40
            || viewportHeight < 40
            || MonitorTopologyCanvas.Width <= 0
            || MonitorTopologyCanvas.Height <= 0)
        {
            return;
        }

        const double breathingRoom = 0.965;
        double fit = Math.Min(
            viewportWidth / MonitorTopologyCanvas.Width,
            viewportHeight / MonitorTopologyCanvas.Height)
            * breathingRoom;
        SetMonitorTopologyZoom(
            Math.Clamp(fit, MinimumTopologyZoom, 2.0));
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                MonitorTopologyScrollViewer.UpdateLayout();
                MonitorTopologyScrollViewer.ScrollToHorizontalOffset(
                    Math.Max(
                        0,
                        (MonitorTopologyScrollViewer.ExtentWidth
                         - MonitorTopologyScrollViewer.ViewportWidth)
                        * 0.5));
                MonitorTopologyScrollViewer.ScrollToVerticalOffset(
                    Math.Max(
                        0,
                        (MonitorTopologyScrollViewer.ExtentHeight
                         - MonitorTopologyScrollViewer.ViewportHeight)
                        * 0.5));
            });
    }

    private void SetMonitorTopologyZoom(double zoom)
    {
        _topologyZoom = Math.Clamp(
            zoom,
            MinimumTopologyZoom,
            3.0);
        MonitorTopologyScaleTransform.ScaleX = _topologyZoom;
        MonitorTopologyScaleTransform.ScaleY = _topologyZoom;
    }

    private void VirtualMonitorDragHandle_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_loading
            || _virtualMonitorVisual is null
            || sender is not UIElement handle)
        {
            return;
        }

        _virtualMonitorDragging = true;
        _topologyAutoFit = false;
        _virtualMonitorDragStart =
            e.GetPosition(MonitorTopologyScrollViewer);
        _virtualMonitorDragLeft =
            Canvas.GetLeft(_virtualMonitorVisual);
        _virtualMonitorDragTop =
            Canvas.GetTop(_virtualMonitorVisual);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void VirtualMonitorDragHandle_MouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_virtualMonitorDragging
            || _virtualMonitorVisual is null)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishVirtualMonitorDrag(sender as UIElement);
            return;
        }

        System.Windows.Point current =
            e.GetPosition(MonitorTopologyScrollViewer);
        double left = _virtualMonitorDragLeft
            + (current.X - _virtualMonitorDragStart.X)
            / _topologyZoom;
        double top = _virtualMonitorDragTop
            + (current.Y - _virtualMonitorDragStart.Y)
            / _topologyZoom;
        ExpandMonitorTopologyForDrag(ref left, ref top);
        Canvas.SetLeft(_virtualMonitorVisual, left);
        Canvas.SetTop(_virtualMonitorVisual, top);
        e.Handled = true;
    }

    private void VirtualMonitorDragHandle_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_virtualMonitorDragging)
            return;
        FinishVirtualMonitorDrag(sender as UIElement);
        e.Handled = true;
    }

    private void VirtualMonitorDragHandle_LostMouseCapture(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_virtualMonitorDragging)
            FinishVirtualMonitorDrag(sender as UIElement);
    }

    private void ExpandMonitorTopologyForDrag(
        ref double left,
        ref double top)
    {
        if (_virtualMonitorVisual is null)
            return;

        const double expansion = 220;
        if (left < TopologyPadding * 0.5)
        {
            foreach (UIElement child in MonitorTopologyCanvas.Children)
                Canvas.SetLeft(child, Canvas.GetLeft(child) + expansion);
            MonitorTopologyCanvas.Width += expansion;
            _topologyOriginX -= expansion / TopologyDeviceScale;
            _virtualMonitorDragLeft += expansion;
            left += expansion;
            MonitorTopologyScrollViewer.ScrollToHorizontalOffset(
                MonitorTopologyScrollViewer.HorizontalOffset
                + expansion * _topologyZoom);
        }
        if (top < TopologyPadding * 0.5)
        {
            foreach (UIElement child in MonitorTopologyCanvas.Children)
                Canvas.SetTop(child, Canvas.GetTop(child) + expansion);
            MonitorTopologyCanvas.Height += expansion;
            _topologyOriginY -= expansion / TopologyDeviceScale;
            _virtualMonitorDragTop += expansion;
            top += expansion;
            MonitorTopologyScrollViewer.ScrollToVerticalOffset(
                MonitorTopologyScrollViewer.VerticalOffset
                + expansion * _topologyZoom);
        }
        if (left + _virtualMonitorVisual.Width
            > MonitorTopologyCanvas.Width - TopologyPadding * 0.5)
        {
            MonitorTopologyCanvas.Width += expansion;
        }
        if (top + _virtualMonitorVisual.Height
            > MonitorTopologyCanvas.Height - TopologyPadding * 0.5)
        {
            MonitorTopologyCanvas.Height += expansion;
        }
    }

    private void FinishVirtualMonitorDrag(UIElement? handle)
    {
        if (!_virtualMonitorDragging
            || _virtualMonitorVisual is null)
        {
            return;
        }
        _virtualMonitorDragging = false;
        handle?.ReleaseMouseCapture();

        MonitorDescriptor? primary = _monitors
            .FirstOrDefault(monitor =>
                !monitor.IsVirtual && monitor.Primary)
            ?? _monitors.FirstOrDefault(monitor => !monitor.IsVirtual);
        if (primary is null)
            return;

        int logicalLeft = (int)Math.Round(
            _topologyOriginX
            + Canvas.GetLeft(_virtualMonitorVisual)
            / TopologyDeviceScale);
        int logicalTop = (int)Math.Round(
            _topologyOriginY
            + Canvas.GetTop(_virtualMonitorVisual)
            / TopologyDeviceScale);
        double previousOriginX = _topologyOriginX;
        double previousOriginY = _topologyOriginY;
        double previousHorizontalOffset =
            MonitorTopologyScrollViewer.HorizontalOffset;
        double previousVerticalOffset =
            MonitorTopologyScrollViewer.VerticalOffset;
        AppSettings current = ReadSettingsFromControls();
        current.VirtualMonitorOffsetX =
            logicalLeft - primary.Bounds.Left;
        current.VirtualMonitorOffsetY =
            logicalTop - primary.Bounds.Top;
        RebuildOutputDevices(current);
        double horizontalCorrection =
            (previousOriginX - _topologyOriginX)
            * TopologyDeviceScale
            * _topologyZoom;
        double verticalCorrection =
            (previousOriginY - _topologyOriginY)
            * TopologyDeviceScale
            * _topologyZoom;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                MonitorTopologyScrollViewer.ScrollToHorizontalOffset(
                    previousHorizontalOffset + horizontalCorrection);
                MonitorTopologyScrollViewer.ScrollToVerticalOffset(
                    previousVerticalOffset + verticalCorrection);
            });
    }

    private void VirtualResolutionCombo_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox input)
            return;

        // Opening the popup temporarily moves keyboard focus outside the
        // editable ComboBox. Rebuilding the monitor canvas at that moment
        // destroys the popup before an item can be chosen. Wait until routed
        // focus has settled and only commit a genuine departure from the field.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!_loading
                    && !input.IsKeyboardFocusWithin
                    && !input.IsDropDownOpen)
                {
                    CommitVirtualResolution(
                        input,
                        preferSelectedItem: false);
                }
            });
    }

    private void VirtualResolutionCombo_DropDownClosed(
        object? sender,
        EventArgs e)
    {
        if (_loading)
            return;
        CommitVirtualResolution(
            sender as ComboBox,
            preferSelectedItem: true);
    }

    private void VirtualResolutionCombo_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitVirtualResolution(
            sender as ComboBox,
            preferSelectedItem: false);
        e.Handled = true;
    }

    private void VirtualResolutionCombo_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (_loading
            || sender is not ComboBox input
            || !input.IsKeyboardFocusWithin)
        {
            return;
        }
        input.ApplyTemplate();
        if (input.Template.FindName(
                "PART_EditableTextBox",
                input) is not TextBox editor
            || !editor.IsKeyboardFocused)
        {
            return;
        }

        string originalText = editor.Text;
        int originalCaret = Math.Clamp(
            editor.CaretIndex,
            0,
            originalText.Length);
        int digitIndex = FindTargetDigit(
            originalText,
            originalCaret);
        if (digitIndex < 0
            || !int.TryParse(
                originalText,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out int current))
        {
            return;
        }

        if (!TryGetVirtualResolutionAxis(input, out bool width))
            return;
        int minimum = width ? 320 : 180;
        int maximum = width ? 7680 : 4320;
        int exponent = Math.Max(
            0,
            DigitExponent(originalText, digitIndex));
        int step = (int)Math.Pow(10, Math.Min(3, exponent));
        int next = Math.Clamp(
            current + (e.Delta > 0 ? step : -step),
            minimum,
            maximum);
        string nextText = next.ToString(
            CultureInfo.InvariantCulture);
        editor.Text = nextText;
        input.Text = nextText;
        editor.CaretIndex = originalCaret >= originalText.Length
            ? nextText.Length
            : Math.Min(originalCaret, nextText.Length);
        if (width)
            VirtualOutputWidthSlider.Value = next;
        else
            VirtualOutputHeightSlider.Value = next;
        _virtualResolutionTimer.Stop();
        _virtualResolutionTimer.Start();
        e.Handled = true;
    }

    private bool IsFocusedVirtualResolutionInput(
        DependencyObject origin)
    {
        DependencyObject? current = origin;
        while (current is not null
               && !ReferenceEquals(
                   current,
                   MonitorTopologyScrollViewer))
        {
            if (ReferenceEquals(
                    current,
                    _virtualMonitorWidthCombo)
                || ReferenceEquals(
                    current,
                    _virtualMonitorHeightCombo))
            {
                return current is ComboBox input
                    && input.IsKeyboardFocusWithin;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void CommitVirtualResolution(
        ComboBox? input,
        bool preferSelectedItem)
    {
        if (_loading
            || input is null
            || !TryGetVirtualResolutionAxis(input, out bool width))
        {
            return;
        }
        _virtualResolutionTimer.Stop();
        int fallback = width
            ? (int)Math.Round(VirtualOutputWidthSlider.Value)
            : (int)Math.Round(VirtualOutputHeightSlider.Value);
        int minimum = width ? 320 : 180;
        int maximum = width ? 7680 : 4320;
        string valueText = preferSelectedItem
            && input.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? input.Text
            : input.Text;
        int parsed = int.TryParse(
            valueText,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out int value)
                ? Math.Clamp(value, minimum, maximum)
                : fallback;
        input.Text = parsed.ToString(CultureInfo.InvariantCulture);
        if (width)
        {
            VirtualOutputWidthSlider.Value = parsed;
        }
        else
        {
            VirtualOutputHeightSlider.Value = parsed;
        }
        RebuildOutputDevices(ReadSettingsFromControls());
    }

    private bool TryGetVirtualResolutionAxis(
        ComboBox input,
        out bool width)
    {
        if (ReferenceEquals(input, _virtualMonitorWidthCombo))
        {
            width = true;
            return true;
        }
        if (ReferenceEquals(input, _virtualMonitorHeightCombo))
        {
            width = false;
            return true;
        }
        width = false;
        return false;
    }

    private void VirtualResolutionTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _virtualResolutionTimer.Stop();
        if (!_loading)
            RebuildOutputDevices(ReadSettingsFromControls());
    }

    private void RebuildOutputDevices(
        AppSettings current,
        bool selectVirtual = false)
    {
        current.Normalize();
        _monitors = OutputDeviceCatalog.Capture(current);
        MonitorTopology.EnsureProfiles(current, _monitors);
        _draftSettings = current.Copy();
        if (selectVirtual
            && _monitors.Any(monitor => monitor.IsVirtual))
        {
            _selectedMonitorId = OutputDeviceCatalog.VirtualMonitorId;
        }
        EnsureSelectedMonitor();
        LoadSettingsCore(current, preserveAppliedSettings: true);
        PublishTopologyPreview();
    }

    private void MonitorOutputWindowButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        if (_loading
            || sender is not FrameworkElement { Tag: string monitorId })
        {
            return;
        }

        AppSettings requested = ReadSettingsFromControls();
        string previousSource =
            requested.VirtualOutputSourceMonitorId;
        bool closesCurrent =
            _openOutputMonitorIds.Contains(monitorId);
        requested.VirtualOutputSourceMonitorId = monitorId;
        _draftSettings = requested.Copy();
        if (!closesCurrent)
        {
            AppSettings visualPreview = requested.Copy();
            visualPreview.VirtualOutputSourceMonitorId =
                previousSource;
            SettingsPreviewed?.Invoke(visualPreview);
        }
        VirtualOutputRequested?.Invoke(requested, !closesCurrent);
        StatusText.Text = closesCurrent
            ? "ОКНО ПОТОКА ЗАКРЫВАЕТСЯ"
            : "КОПИЯ ПОТОКА ОТКРЫВАЕТСЯ В ОТДЕЛЬНОМ ОКНЕ";
        RefreshMonitorVisuals();
    }

    private void MonitorVisual_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (IsInteractiveMonitorChild(
                e.OriginalSource as DependencyObject,
                sender as DependencyObject))
        {
            return;
        }
        e.Handled = true;
        if (_loading
            || sender is not FrameworkElement { Tag: string monitorId }
            || string.Equals(
                monitorId,
                _selectedMonitorId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _draftSettings = ReadSettingsFromControls();
        _selectedMonitorId = monitorId;
        LoadSettingsCore(_draftSettings, preserveAppliedSettings: true);
    }

    private static bool IsInteractiveMonitorChild(
        DependencyObject? origin,
        DependencyObject? monitorVisual)
    {
        DependencyObject? current = origin;
        while (current is not null
               && !ReferenceEquals(current, monitorVisual))
        {
            if (current is ComboBox
                or TextBox
                or System.Windows.Controls.Button)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private List<MonitorRouteChoice> CreateRouteChoices(
        MonitorRouteDomain domain,
        string selectedMonitorId)
    {
        string isolated = domain == MonitorRouteDomain.Flow
            ? "ИЗОЛИРОВАННЫЙ"
            : "ИЗОЛИРОВАТЬ";
        string disabled = domain == MonitorRouteDomain.Flow
            ? "ОТКЛЮЧЁН"
            : "ОТКЛЮЧЕНА";
        List<MonitorRouteChoice> result =
        [
            new MonitorRouteChoice
            {
                Mode = MonitorLinkMode.Isolated,
                Label = isolated
            },
            new MonitorRouteChoice
            {
                Mode = MonitorLinkMode.Disabled,
                Label = disabled
            }
        ];
        foreach (MonitorDescriptor source in _monitors.Where(monitor =>
                     !string.Equals(
                         monitor.Id,
                         selectedMonitorId,
                         StringComparison.OrdinalIgnoreCase)
                     && IsRouteSourceAvailable(monitor.Id, domain)))
        {
            result.Add(new MonitorRouteChoice
            {
                Mode = MonitorLinkMode.Relay,
                SourceMonitorId = source.Id,
                Label = $"РЕТРАНСЛИРОВАТЬ — {source.Label}"
            });
        }
        foreach (MonitorDescriptor source in _monitors.Where(monitor =>
                     !string.Equals(
                         monitor.Id,
                         selectedMonitorId,
                         StringComparison.OrdinalIgnoreCase)
                     && IsRouteSourceAvailable(monitor.Id, domain)))
        {
            result.Add(new MonitorRouteChoice
            {
                Mode = MonitorLinkMode.Extend,
                SourceMonitorId = source.Id,
                Label = $"РАСШИРИТЬ — {source.Label}"
            });
        }
        return result;
    }

    private bool IsRouteSourceAvailable(
        string monitorId,
        MonitorRouteDomain domain)
    {
        MonitorProfile? profile = MonitorTopology.Find(
            _draftSettings.MonitorProfiles,
            monitorId);
        return profile is not null
            && MonitorTopology.CanBeRouteSource(profile, domain);
    }

    private static MonitorRouteChoice? MatchRouteChoice(
        IEnumerable<MonitorRouteChoice> choices,
        MonitorLinkMode mode,
        string sourceMonitorId) =>
        choices.FirstOrDefault(choice =>
            choice.Mode == mode
            && (mode is MonitorLinkMode.Isolated or MonitorLinkMode.Disabled
                || string.Equals(
                    choice.SourceMonitorId,
                    sourceMonitorId,
                    StringComparison.OrdinalIgnoreCase)));

    private void MonitorFlowModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        e.Handled = true;
        ApplyMonitorRouteSelection(
            MonitorRouteDomain.Flow,
            MonitorFlowModeCombo.SelectedItem as MonitorRouteChoice);
    }

    private void MonitorDatabaseModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        e.Handled = true;
        ApplyMonitorRouteSelection(
            MonitorRouteDomain.Database,
            MonitorDatabaseModeCombo.SelectedItem as MonitorRouteChoice);
    }

    private void ApplyMonitorRouteSelection(
        MonitorRouteDomain domain,
        MonitorRouteChoice? choice,
        string? targetMonitorId = null)
    {
        if (_loading || choice is null)
            return;

        AppSettings current = ReadSettingsFromControls();
        string monitorId = string.IsNullOrWhiteSpace(targetMonitorId)
            ? _selectedMonitorId
            : targetMonitorId;
        MonitorTopology.SetRoute(
            current.MonitorProfiles,
            _monitors,
            domain,
            monitorId,
            choice.Mode,
            choice.SourceMonitorId);
        if (domain == MonitorRouteDomain.Flow
            && OutputDeviceCatalog.IsVirtual(monitorId))
        {
            current.VirtualMonitorEnabled =
                choice.Mode != MonitorLinkMode.Disabled;
            if (!current.VirtualMonitorEnabled
                && _openOutputMonitorIds.Contains(monitorId))
            {
                current.VirtualOutputSourceMonitorId =
                    monitorId;
                VirtualOutputRequested?.Invoke(current, false);
            }
        }
        LoadSettingsCore(current, preserveAppliedSettings: true);
        PublishTopologyPreview();
        StatusText.Text =
            $"МАРШРУТ ОБНОВЛЁН // {choice.Label}";
    }

    private void OpenFlowSourceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenRouteSource(MonitorRouteDomain.Flow);

    private void OpenDatabaseSourceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenRouteSource(MonitorRouteDomain.Database);

    private void OpenRouteSource(MonitorRouteDomain domain)
    {
        MonitorProfile selected = SelectedMonitorProfile(_draftSettings);
        string source = domain == MonitorRouteDomain.Flow
            ? selected.FlowSourceMonitorId
            : selected.DatabaseSourceMonitorId;
        if (string.IsNullOrWhiteSpace(source))
            return;
        if (!_monitors.Any(monitor => string.Equals(
                monitor.Id,
                source,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _draftSettings = ReadSettingsFromControls();
        _selectedMonitorId = source;
        LoadSettingsCore(_draftSettings, preserveAppliedSettings: true);
    }

    private void PublishTopologyPreview()
    {
        _virtualResolutionTimer.Stop();
        _previewTimer.Stop();
        _fontPreviewTimer.Stop();
        _colorPreviewTimer.Stop();
        if (!HasInvalidNumericInput())
            SettingsPreviewed?.Invoke(ReadLivePreviewSettings());
    }

    private void UpdateMonitorRouteNotices()
    {
        if (FlowRoutingNotice is null)
            return;
        MonitorProfile selected = SelectedMonitorProfile(_draftSettings);
        UpdateFlowRouteNotice(selected);
        UpdateDatabaseRouteNotice(selected);
    }

    private void UpdateFlowRouteNotice(MonitorProfile profile)
    {
        bool isolated = profile.FlowMode == MonitorLinkMode.Isolated;
        FlowRoutingNotice.Visibility = isolated
            ? Visibility.Collapsed
            : Visibility.Visible;
        CurveKindCombo.Visibility = isolated
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (CalibratorResetButton is not null)
        {
            CalibratorResetButton.Visibility = isolated
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (isolated)
        {
            RefreshCurveEditor();
            return;
        }
        CurveProfilePanel.Visibility = Visibility.Collapsed;
        CurveCanvasBorder.Visibility = Visibility.Collapsed;
        TerminalSettingsPanel.Visibility = Visibility.Collapsed;
        CurveEditingHint.Visibility = Visibility.Collapsed;
        TerminalEditingHint.Visibility = Visibility.Collapsed;
        bool disabled = profile.FlowMode == MonitorLinkMode.Disabled;
        MonitorDescriptor? source = _monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.Id,
                profile.FlowSourceMonitorId,
                StringComparison.OrdinalIgnoreCase));
        FlowRoutingNoticeTitle.Text = disabled
            ? "ПОТОК ДАННЫХ ОТКЛЮЧЁН"
            : profile.FlowMode == MonitorLinkMode.Extend
                ? "ПОТОК РАСШИРЯЕТ ДРУГОЙ ЭКРАН"
                : "ПОТОК РЕТРАНСЛИРУЕТСЯ";
        FlowRoutingNoticeText.Text = disabled
            ? "На выбранном устройстве код не выводится."
            : $"Параметры потока задаются на устройстве «{source?.Label ?? profile.FlowSourceMonitorId}».";
        OpenFlowSourceButton.Visibility = disabled
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateDatabaseRouteNotice(MonitorProfile profile)
    {
        if (DatabaseRoutingNotice is null)
            return;
        bool isolated = profile.DatabaseMode == MonitorLinkMode.Isolated;
        DatabaseRoutingNotice.Visibility = isolated
            ? Visibility.Collapsed
            : Visibility.Visible;
        DatabaseHeaderPanel.Visibility = isolated
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!isolated)
            DatabaseContentPanel.Visibility = Visibility.Collapsed;
        else
            UpdateCollapsibleSections();

        if (isolated)
            return;
        bool disabled = profile.DatabaseMode == MonitorLinkMode.Disabled;
        MonitorDescriptor? source = _monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.Id,
                profile.DatabaseSourceMonitorId,
                StringComparison.OrdinalIgnoreCase));
        DatabaseRoutingNoticeTitle.Text = disabled
            ? "БАЗА ДАННЫХ ОТКЛЮЧЕНА"
            : profile.DatabaseMode == MonitorLinkMode.Extend
                ? "ОБРАЗЫ РАСШИРЯЮТ ДРУГОЙ ЭКРАН"
                : "БАЗА ДАННЫХ РЕТРАНСЛИРУЕТСЯ";
        DatabaseRoutingNoticeText.Text = disabled
            ? "Поток остаётся активным, но изображения на этом устройстве не проявляются."
            : $"Плейлист, порядок и параметры образов задаются на устройстве «{source?.Label ?? profile.DatabaseSourceMonitorId}».";
        OpenDatabaseSourceButton.Visibility = disabled
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SynchronizeLegacySettings(AppSettings settings)
    {
        MonitorSettingsSynchronizer.SynchronizePrimary(
            settings,
            _monitors);
    }

    private void RefreshPresetCatalog(string requestedPresetId)
    {
        bool previousLoading = _loading;
        _loading = true;
        _updatingPresetUi = true;
        try
        {
            _presets = _presetStore.LoadAll().ToList();
            PresetCombo.Items.Clear();
            PresetChoice operatorChoice = new();
            foreach (OperatorPreset preset in _presets.OrderBy(
                         preset => preset.Name,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                PresetCombo.Items.Add(new PresetChoice
                {
                    Label = preset.Name,
                    Preset = preset
                });
            }
            PresetCombo.Items.Add(operatorChoice);
            foreach (BuiltInPreset builtIn in BuiltInPresetCatalog.Items
                         .OrderBy(
                             preset => preset.Name,
                             StringComparer.CurrentCultureIgnoreCase))
            {
                PresetCombo.Items.Add(new PresetChoice
                {
                    Label = builtIn.Name,
                    BuiltInPreset = builtIn
                });
            }

            PresetChoice selected = PresetCombo.Items
                .OfType<PresetChoice>()
                .FirstOrDefault(choice => string.Equals(
                    choice.Id,
                    requestedPresetId,
                    StringComparison.OrdinalIgnoreCase))
                ?? operatorChoice;
            PresetCombo.SelectedItem = selected;
            PresetCombo.IsEnabled = PresetCombo.Items.Count > 1;
            _selectedPresetId = selected.Id;
            DeletePresetButton.IsEnabled = selected.Preset is not null;
        }
        finally
        {
            _updatingPresetUi = false;
            _loading = previousLoading;
        }
        UpdatePresetActionButtons();
    }

    private void PresetCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loading
            || _updatingPresetUi
            || PresetCombo.SelectedItem is not PresetChoice choice)
        {
            return;
        }

        string requestedId = choice.Id;
        if (string.Equals(
            requestedId,
            _selectedPresetId,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppSettings currentDraft = ReadSettingsFromControls();
        _selectedPresetId = requestedId;
        DeletePresetButton.IsEnabled = choice.Preset is not null;
        if (choice.BuiltInPreset is not null)
        {
            LoadDraft(ApplyBuiltInPreset(
                choice.BuiltInPreset,
                currentDraft));
            StatusText.Text =
                $"ЭТАЛОН «{choice.BuiltInPreset.Name.ToUpperInvariant()}» "
                + "ЗАГРУЖЕН В ПРЕДПРОСМОТР";
        }
        else if (choice.Preset is not null)
        {
            LoadDraft(ApplyPreset(choice.Preset, currentDraft));
            StatusText.Text =
                $"ПРЕСЕТ «{choice.Preset.Name.ToUpperInvariant()}» "
                + "ЗАГРУЖЕН В ПРЕДПРОСМОТР";
        }
        else
        {
            QueuePreview();
            StatusText.Text =
                "ОПЕРАТОРСКИЙ РЕЖИМ // ПРЕСЕТ НЕ ВЫБРАН";
        }
    }

    private void PresetCombo_DropDownOpened(object sender, EventArgs e) =>
        RefreshPresetCatalog(_selectedPresetId);

    private void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticLog.Write("Оператор открыл создание глобального пресета.");
        if (HasInvalidNumericInput())
        {
            StatusText.Text =
                "ПРЕСЕТ НЕ СОЗДАН // ПРОВЕРЬТЕ ЧИСЛОВЫЕ ПОЛЯ";
            return;
        }

        PresetNameDialog dialog = new()
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _loading = true;
            CommitAllNumericInputs();
            _loading = false;
            AppSettings draft = ReadSettingsFromControls();
            OperatorPreset preset = _presetStore.Create(
                dialog.PresetName,
                draft,
                _presets);
            _selectedPresetId = preset.Id;
            RefreshPresetCatalog(preset.Id);
            QueuePreview();
            StatusText.Text =
                $"ПРЕСЕТ «{preset.Name.ToUpperInvariant()}» СОЗДАН";
        }
        catch (Exception exception)
        {
            _loading = false;
            DiagnosticLog.Write("Не удалось создать операторский пресет.", exception);
            StatusText.Text =
                $"ПРЕСЕТ НЕ СОЗДАН // {exception.Message.ToUpperInvariant()}";
        }
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorPreset? preset = CurrentPreset();
        if (preset is null)
            return;
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            $"Удалить пресет «{preset.Name}»?\n\n"
            + "Текущие параметры на экране останутся без изменений.",
            "Wallpaper Matrix",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _presetStore.Delete(preset);
            _selectedPresetId = "";
            RefreshPresetCatalog("");
            QueuePreview();
            StatusText.Text =
                $"ПРЕСЕТ «{preset.Name.ToUpperInvariant()}» УДАЛЁН";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Не удалось удалить операторский пресет.", exception);
            StatusText.Text =
                $"ПРЕСЕТ НЕ УДАЛЁН // {exception.Message.ToUpperInvariant()}";
        }
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorPreset? preset = CurrentPreset();
        if (preset is null || HasInvalidNumericInput())
            return;

        try
        {
            _loading = true;
            CommitAllNumericInputs();
            _loading = false;
            AppSettings draft = ReadSettingsFromControls();
            _presetStore.Save(preset, draft);
            RefreshPresetCatalog(preset.Id);
            UpdateDraftStatus();
            StatusText.Text = _hasPendingChanges
                ? $"ПРЕСЕТ «{preset.Name.ToUpperInvariant()}» СОХРАНЁН "
                    + "// ПАРАМЕТРЫ ЕЩЁ НЕ ПРИМЕНЕНЫ"
                : $"ПРЕСЕТ «{preset.Name.ToUpperInvariant()}» СОХРАНЁН";
        }
        catch (Exception exception)
        {
            _loading = false;
            DiagnosticLog.Write("Не удалось сохранить операторский пресет.", exception);
            StatusText.Text =
                $"ПРЕСЕТ НЕ СОХРАНЁН // {exception.Message.ToUpperInvariant()}";
        }
    }

    private void ResetPresetButton_Click(object sender, RoutedEventArgs e)
    {
        BuiltInPreset? builtIn = CurrentBuiltInPreset();
        if (builtIn is not null)
        {
            AppSettings builtInDraft = ReadSettingsFromControls();
            LoadDraft(ApplyBuiltInPreset(builtIn, builtInDraft));
            StatusText.Text =
                $"ВОССТАНОВЛЕН ЭТАЛОН «{builtIn.Name.ToUpperInvariant()}»";
            return;
        }

        OperatorPreset? preset = CurrentPreset();
        if (preset is null)
            return;
        AppSettings currentDraft = ReadSettingsFromControls();
        LoadDraft(ApplyPreset(preset, currentDraft));
        StatusText.Text =
            $"ВОССТАНОВЛЕН ПРЕСЕТ «{preset.Name.ToUpperInvariant()}»";
    }

    private bool TrySaveActivePreset(AppSettings settings)
    {
        if (CurrentBuiltInPreset() is not null)
            return true;

        OperatorPreset? preset = CurrentPreset();
        if (preset is null
            || PresetEquivalentForCurrentTopology(settings, preset))
        {
            return true;
        }

        try
        {
            _presetStore.Save(preset, settings);
            RefreshPresetCatalog(preset.Id);
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Применение остановлено: текущий пресет не сохранён.",
                exception);
            StatusText.Text =
                $"ПРИМЕНЕНИЕ ОСТАНОВЛЕНО // {exception.Message.ToUpperInvariant()}";
            return false;
        }
    }

    private AppSettings ApplyPreset(
        OperatorPreset preset,
        AppSettings current)
    {
        AppSettings topology = current.Copy();
        topology.VirtualMonitorEnabled =
            preset.Settings.VirtualMonitorEnabled;
        topology.VirtualOutputWidth =
            preset.Settings.VirtualOutputWidth;
        topology.VirtualOutputHeight =
            preset.Settings.VirtualOutputHeight;
        topology.VirtualMonitorOffsetX =
            preset.Settings.VirtualMonitorOffsetX;
        topology.VirtualMonitorOffsetY =
            preset.Settings.VirtualMonitorOffsetY;
        topology.VirtualMonitorDock =
            preset.Settings.VirtualMonitorDock;
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(topology);
        AppSettings result = MonitorPresetAdapter.Adapt(
            preset.Settings,
            current,
            monitors);
        OperatorPlaylistBinding.Apply(result, current);
        result.WelcomeShown = current.WelcomeShown;
        result.ActivePresetId = preset.Id;
        result.Normalize();
        return result;
    }

    private AppSettings ApplyBuiltInPreset(
        BuiltInPreset preset,
        AppSettings current)
    {
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(current);
        AppSettings result = BuiltInPresetCatalog.Apply(
            preset,
            current,
            monitors);
        result.WelcomeShown = current.WelcomeShown;
        result.ActivePresetId = preset.Id;
        result.Normalize();
        return result;
    }

    private OperatorPreset? CurrentPreset() =>
        _presets.FirstOrDefault(preset => string.Equals(
            preset.Id,
            _selectedPresetId,
            StringComparison.OrdinalIgnoreCase));

    private BuiltInPreset? CurrentBuiltInPreset() =>
        BuiltInPresetCatalog.Find(_selectedPresetId);

    private void UpdatePresetActionButtons()
    {
        if (SavePresetButton is null
            || ResetPresetButton is null
            || DeletePresetButton is null)
        {
            return;
        }

        OperatorPreset? preset = CurrentPreset();
        BuiltInPreset? builtIn = CurrentBuiltInPreset();
        DeletePresetButton.IsEnabled = preset is not null;
        bool hasUserPresetChanges = preset is not null
            && !HasInvalidNumericInput()
            && !PresetEquivalentForCurrentTopology(
                ReadSettingsFromControls(),
                preset);
        bool hasBuiltInChanges = builtIn is not null
            && !HasInvalidNumericInput()
            && !BuiltInPresetEquivalent(
                ReadSettingsFromControls(),
                builtIn);
        SavePresetButton.Visibility = hasUserPresetChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResetPresetButton.Visibility =
            hasUserPresetChanges || hasBuiltInChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        SavePresetButton.ToolTip =
            "Сохранить изменения в выбранный пользовательский пресет";
        ResetPresetButton.ToolTip = builtIn is not null
            ? "Вернуть параметры встроенного эталона"
            : "Вернуть параметры выбранного пресета";
    }

    private bool PresetEquivalentForCurrentTopology(
        AppSettings settings,
        OperatorPreset preset)
    {
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(settings);
        AppSettings baseline = MonitorPresetAdapter.Adapt(
            preset.Settings,
            settings,
            monitors);
        bool playlistBindingsMatch =
            OperatorPlaylistBinding.Matches(
                baseline,
                settings);
        OperatorPlaylistBinding.Apply(baseline, settings);
        return playlistBindingsMatch
            && AppSettingsComparer.PresetEquivalent(
            settings,
            baseline);
    }

    private bool BuiltInPresetEquivalent(
        AppSettings settings,
        BuiltInPreset preset)
    {
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(settings);
        AppSettings baseline = BuiltInPresetCatalog.Apply(
            preset,
            settings,
            monitors);
        return AppSettingsComparer.PresetEquivalent(
            settings,
            baseline);
    }

    private void NewPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePlaylist playlist = new()
        {
            Name = $"Плейлист {_playlists.Count + 1}"
        };
        _playlists.Add(playlist);
        _activePlaylistId = playlist.Id;
        RefreshPlaylistUi();
        QueuePreview();
    }

    private void SavePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePlaylist playlist = CurrentPlaylist();
        playlist.Name = string.IsNullOrWhiteSpace(PlaylistNameTextBox.Text)
            ? "Плейлист без имени"
            : PlaylistNameTextBox.Text.Trim();
        List<ImagePlaylist> savedPlaylists = _playlists
            .Select(item => item.Copy())
            .ToList();
        foreach (ImagePlaylist savedPlaylist in savedPlaylists)
            savedPlaylist.Normalize();
        _playlists = savedPlaylists
            .Select(item => item.Copy())
            .ToList();
        RefreshPlaylistUi();
        _source.ImagePlaylists = savedPlaylists
            .Select(item => item.Copy())
            .ToList();
        AppSettings sourceDisplay = SelectedMonitorSettings(_source);
        sourceDisplay.ActiveImagePlaylistId = _activePlaylistId;
        SynchronizeLegacySettings(_source);
        AppSettings liveDraft = ReadSettingsFromControls();
        _draftSettings = liveDraft.Copy();
        PlaylistsSaved?.Invoke(liveDraft);
        _playlistFileVersion = _playlistStore.FileVersion();
        UpdateDraftStatus();
        QueuePreview();
        StatusText.Text = _hasPendingChanges
            ? "ПЛЕЙЛИСТ СОХРАНЁН // ОСТАЛИСЬ НЕПРИМЕНЁННЫЕ ПАРАМЕТРЫ"
            : "ПЛЕЙЛИСТ СОХРАНЁН // ДАННЫЕ СИНХРОНИЗИРОВАНЫ";
    }

    private void DeletePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePlaylist current = CurrentPlaylist();
        _playlists.Remove(current);
        if (_playlists.Count == 0)
            _playlists.Add(new ImagePlaylist());
        _activePlaylistId = _playlists[0].Id;
        RefreshPlaylistUi();
        QueuePreview();
    }

    private void PlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PlaylistCombo.SelectedItem is not ComboBoxItem item)
            return;
        _activePlaylistId = item.Tag?.ToString() ?? _playlists[0].Id;
        RefreshPlaylistEditor();
        QueuePreview();
        e.Handled = true;
    }

    private void PlaylistPlacementCheck_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_loading)
            return;

        ImagePlacement placement = CurrentPlaylist().Placement;
        placement.FillHorizontal =
            PlaylistFillHorizontalCheck.IsChecked == true;
        placement.FillVertical =
            PlaylistFillVerticalCheck.IsChecked == true;
        QueuePreview();
    }

    private void PlaylistCombo_DropDownOpened(
        object? sender,
        EventArgs e)
    {
        if (!ReloadPlaylistsFromStorage())
            return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => PlaylistCombo.IsDropDownOpen = true);
    }

    private void ImageModeCheck_Click(
        object sender,
        RoutedEventArgs e) =>
        ReloadPlaylistsFromStorage();

    private bool ReloadPlaylistsFromStorage()
    {
        if (_loading)
            return false;

        string fileVersion = _playlistStore.FileVersion();
        if (string.Equals(
                fileVersion,
                "unavailable",
                StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(
                fileVersion,
                _playlistFileVersion,
                StringComparison.Ordinal))
        {
            return false;
        }

        AppSettings reloaded = ReadSettingsFromControls();
        _playlistStore.LoadInto(reloaded);
        CopyPlaylistState(_source, reloaded);
        _draftSettings = reloaded.Copy();

        AppSettings display = SelectedMonitorSettings(_draftSettings);
        _playlists = _draftSettings.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        _activePlaylistId = display.ActiveImagePlaylistId;
        RefreshPlaylistUi();
        _playlistFileVersion = _playlistStore.FileVersion();
        PlaylistsReloaded?.Invoke(reloaded);
        UpdateDraftStatus();
        StatusText.Text =
            "ФАЙЛ ПЛЕЙЛИСТОВ ПЕРЕЧИТАН // БАЗА ДАННЫХ СИНХРОНИЗИРОВАНА";
        return true;
    }

    private static void CopyPlaylistState(
        AppSettings target,
        AppSettings source)
    {
        target.ImagePlaylists = source.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        target.ActiveImagePlaylistId = source.ActiveImagePlaylistId;
        foreach (MonitorProfile sourceProfile in source.MonitorProfiles)
        {
            MonitorProfile? targetProfile = MonitorTopology.Find(
                target.MonitorProfiles,
                sourceProfile.MonitorId);
            if (targetProfile is null)
                continue;
            targetProfile.Settings.ActiveImagePlaylistId =
                sourceProfile.Settings.ActiveImagePlaylistId;
        }
        target.Normalize();
    }

    private void PlaylistNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _playlists.Count == 0)
            return;
        CurrentPlaylist().Name = PlaylistNameTextBox.Text;
        QueuePreview();
    }

    private void AddPlaylistFilesButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "Добавить изображения в плейлист",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|Все файлы|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            AddPathsToPlaylist(dialog.FileNames);
    }

    private void AddPlaylistFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using System.Windows.Forms.FolderBrowserDialog dialog = new()
        {
            Description = "Добавить изображения из папки в активный плейлист",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            AddPathsToPlaylist([dialog.SelectedPath]);
    }

    private (FrameworkElement Section, ToggleButton Bookmark)[]
        SectionNavigationTargets() =>
        [
            (OutputDevicesSection, OutputDevicesBookmark),
            (FlowCalibratorSection, FlowCalibratorBookmark),
            (DatabaseSection, DatabaseBookmark),
            (AttackSection, AttackBookmark),
            (SystemSection, SystemBookmark)
        ];

    private static bool IsSectionBookmark(ToggleButton toggle) =>
        toggle.Tag is string target
        && target is "OutputDevicesSection"
            or "FlowCalibratorSection"
            or "DatabaseSection"
            or "AttackSection"
            or "SystemSection";

    private void SectionBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton bookmark)
            return;

        (FrameworkElement Section, ToggleButton Bookmark)? destination =
            SectionNavigationTargets()
                .FirstOrDefault(item => ReferenceEquals(item.Bookmark, bookmark));
        if (destination is null)
            return;

        System.Windows.Point visiblePosition =
            destination.Value.Section.TranslatePoint(
            new System.Windows.Point(0, 0),
            MainScrollViewer);
        _sectionNavigationStartOffset = MainScrollViewer.VerticalOffset;
        _sectionNavigationTargetOffset = Math.Clamp(
            _sectionNavigationStartOffset + visiblePosition.Y - 18,
            0,
            MainScrollViewer.ScrollableHeight);
        _sectionNavigationClock.Restart();
        _sectionNavigationTimer.Start();
        UpdateSectionNavigationSelection(bookmark);
        e.Handled = true;
    }

    private void SectionNavigationTimer_Tick(object? sender, EventArgs e)
    {
        const double durationSeconds = 0.28;
        double progress = Math.Clamp(
            _sectionNavigationClock.Elapsed.TotalSeconds / durationSeconds,
            0,
            1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        MainScrollViewer.ScrollToVerticalOffset(
            _sectionNavigationStartOffset
            + ((_sectionNavigationTargetOffset
                - _sectionNavigationStartOffset) * eased));
        if (progress < 1)
            return;

        _sectionNavigationTimer.Stop();
        MainScrollViewer.ScrollToVerticalOffset(
            _sectionNavigationTargetOffset);
        UpdateSectionNavigationSelection();
    }

    private void MainScrollViewer_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (!_updatingSectionNavigation)
            UpdateSectionNavigationSelection();
    }

    private void UpdateSectionNavigationSelection(
        ToggleButton? forcedBookmark = null)
    {
        if (!IsLoaded || MainScrollViewer is null)
            return;

        (FrameworkElement Section, ToggleButton Bookmark)[] targets =
            SectionNavigationTargets();
        ToggleButton active = forcedBookmark ?? targets[0].Bookmark;
        if (forcedBookmark is null)
        {
            double viewportTop = MainScrollViewer.VerticalOffset;
            double viewportBottom = viewportTop
                + MainScrollViewer.ViewportHeight;
            double largestVisibleArea = double.NegativeInfinity;
            foreach ((FrameworkElement section, ToggleButton bookmark) in targets)
            {
                System.Windows.Point visiblePosition = section.TranslatePoint(
                    new System.Windows.Point(0, 0),
                    MainScrollViewer);
                double documentTop = MainScrollViewer.VerticalOffset
                    + visiblePosition.Y;
                double documentBottom = documentTop + section.ActualHeight;
                double visibleArea = Math.Max(
                    0,
                    Math.Min(documentBottom, viewportBottom)
                    - Math.Max(documentTop, viewportTop));
                if (visibleArea > largestVisibleArea)
                {
                    largestVisibleArea = visibleArea;
                    active = bookmark;
                }
            }
        }

        _updatingSectionNavigation = true;
        try
        {
            foreach ((_, ToggleButton bookmark) in targets)
                bookmark.IsChecked = ReferenceEquals(bookmark, active);
        }
        finally
        {
            _updatingSectionNavigation = false;
        }
    }

    private void SettingsWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PlaylistList is null
            || MonitorTopologyScrollViewer is null
            || CurveKindCombo is null
            || MainScrollViewer is null
            || e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        _sectionNavigationTimer.Stop();

        bool overPlaylist = IsDescendantOf(origin, PlaylistList);
        bool overTopology = IsDescendantOf(
            origin,
            MonitorTopologyScrollViewer);
        bool overFocusedInput = IsFocusedWheelInput(origin);
        long now = Environment.TickCount64;
        if (now - _lastWheelTick > 340)
        {
            _wheelGestureTarget = overFocusedInput
                ? WheelGestureTarget.FocusedInput
                : overTopology
                    ? WheelGestureTarget.MonitorTopology
                    : overPlaylist
                        ? WheelGestureTarget.Playlist
                        : WheelGestureTarget.MainForm;
        }
        _lastWheelTick = now;

        // Nested lists and the zoomable topology keep a gesture only when it
        // started there. A page scroll that crosses either area continues to
        // move the long form until the operator pauses the wheel.
        if (_wheelGestureTarget != WheelGestureTarget.MainForm)
        {
            bool stillOverGestureTarget = _wheelGestureTarget switch
            {
                WheelGestureTarget.Playlist => overPlaylist,
                WheelGestureTarget.MonitorTopology => overTopology,
                WheelGestureTarget.FocusedInput => overFocusedInput,
                _ => false
            };
            if (!stillOverGestureTarget)
                e.Handled = true;
            return;
        }

        double step = e.Delta / 120.0 * 52.0;
        MainScrollViewer.ScrollToVerticalOffset(Math.Clamp(
            MainScrollViewer.VerticalOffset - step,
            0,
            MainScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static bool IsFocusedWheelInput(DependencyObject origin)
    {
        TextBox? textBox = FindAncestor<TextBox>(origin);
        if (textBox?.IsKeyboardFocusWithin == true)
            return true;
        ComboBox? comboBox = FindAncestor<ComboBox>(origin);
        return comboBox?.IsEditable == true
            && comboBox.IsKeyboardFocusWithin;
    }

    private void PlaylistList_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void PlaylistList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PlaylistNameColumn is null || e.NewSize.Width <= 260)
            return;
        const double enabled = 48;
        const double resolution = 106;
        const double actions = 78;
        const double verticalScrollBar = 10;
        double flexible = Math.Max(
            120,
            e.NewSize.Width - enabled - resolution - actions - verticalScrollBar - 4);
        PlaylistEnabledColumn.Width = new DataGridLength(enabled);
        PlaylistNameColumn.Width = new DataGridLength(flexible * 0.40);
        PlaylistLocationColumn.Width = new DataGridLength(flexible * 0.60);
        PlaylistResolutionColumn.Width = new DataGridLength(resolution);
        PlaylistActionsColumn.Width = new DataGridLength(actions);
    }

    private async void PlaylistList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject origin
            || FindAncestor<System.Windows.Controls.CheckBox>(origin) is not null
            || FindAncestor<System.Windows.Controls.Button>(origin) is not null
            || FindAncestor<System.Windows.Controls.DataGridRow>(origin)?.Item
                is not ImagePlaylistEntry entry)
        {
            return;
        }

        e.Handled = true;
        if (Interlocked.CompareExchange(
                ref _externalImageLaunchInProgress,
                1,
                0) != 0)
        {
            StatusText.Text = "ПРОСМОТРЩИК УЖЕ ЗАПУСКАЕТСЯ";
            return;
        }

        try
        {
            entry.RefreshAvailability();
            if (!entry.Exists)
                throw new FileNotFoundException("Файл изображения не найден.", entry.Path);
            string path = entry.Path;
            await Task.Run(() =>
            {
                using Process? launched = Process.Start(
                    new ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
            });
            StatusText.Text = $"ОБРАЗ ОТКРЫТ // {entry.DisplayName}";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                $"Не удалось открыть изображение во внешнем просмотрщике: {entry.Path}",
                exception);
            StatusText.Text = $"НЕ УДАЛОСЬ ОТКРЫТЬ ОБРАЗ // {entry.DisplayName}";
        }
        finally
        {
            Interlocked.Exchange(
                ref _externalImageLaunchInProgress,
                0);
        }
    }

    private void PlaylistList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            AddPathsToPlaylist(paths);
        e.Handled = true;
    }

    private void PlaylistList_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin
            || FindAncestor<System.Windows.Controls.DataGridRow>(origin)
                is not System.Windows.Controls.DataGridRow row
            || row.IsSelected)
        {
            return;
        }

        PlaylistList.UnselectAll();
        row.IsSelected = true;
        PlaylistList.CurrentItem = row.Item;
    }

    private void PlaylistEntryEnabled_Click(object sender, RoutedEventArgs e)
    {
        // Update the model explicitly before scheduling the live preview.
        // Depending on the WPF binding event order, Click can otherwise see
        // the previous IsChecked value and leave the slideshow with a stale
        // enabled-set until another control changes.
        if (sender is System.Windows.Controls.CheckBox checkBox
            && checkBox.DataContext is ImagePlaylistEntry entry)
        {
            entry.Enabled = checkBox.IsChecked == true;
        }
        QueuePreview();
    }

    private void PlayPlaylistEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImagePlaylistEntry entry)
            return;
        entry.RefreshAvailability();
        if (!entry.Exists)
        {
            StatusText.Text = $"ОБРАЗ НЕДОСТУПЕН // {entry.DisplayName}";
            e.Handled = true;
            return;
        }
        ImageModeCheck.IsChecked = true;
        AppSettings preview = ReadSettingsFromControls();
        ImageRequested?.Invoke(preview, entry.Path, _selectedMonitorId);
        StatusText.Text = $"ОБРАЗ ПЕРЕХВАЧЕН // {entry.DisplayName} // ДАЛЕЕ ПО ПЛЕЙЛИСТУ";
        e.Handled = true;
    }

    private void DeletePlaylistEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImagePlaylistEntry entry)
            return;
        CurrentPlaylist().Entries.Remove(entry);
        RefreshPlaylistEntries();
        QueuePreview();
        StatusText.Text = $"СТРОКА УДАЛЕНА // {entry.DisplayName} // ИЗМЕНЕНИЯ НЕ ПРИМЕНЕНЫ";
        e.Handled = true;
    }

    private void SelectAllPlaylistEntries_Click(object sender, RoutedEventArgs e) =>
        PlaylistList.SelectAll();

    private void InvertPlaylistSelection_Click(object sender, RoutedEventArgs e)
    {
        HashSet<ImagePlaylistEntry> selected = SelectedPlaylistEntries().ToHashSet();
        PlaylistList.UnselectAll();
        foreach (ImagePlaylistEntry entry in CurrentPlaylist().Entries)
        {
            if (!selected.Contains(entry))
                PlaylistList.SelectedItems.Add(entry);
        }
    }

    private void ClearPlaylistSelection_Click(object sender, RoutedEventArgs e) =>
        PlaylistList.UnselectAll();

    private void EnableSelectedPlaylistEntries_Click(object sender, RoutedEventArgs e) =>
        UpdatePlaylistEntryStates(PlaylistEntryStateOperation.EnableSelected);

    private void DisableSelectedPlaylistEntries_Click(object sender, RoutedEventArgs e) =>
        UpdatePlaylistEntryStates(PlaylistEntryStateOperation.DisableSelected);

    private void SortPlaylistEntries_Click(object sender, RoutedEventArgs e)
    {
        ImagePlaylist playlist = CurrentPlaylist();
        playlist.Entries = (_sortNameDescending
                ? playlist.Entries
                    .OrderByDescending(
                        entry => entry.DisplayName,
                        StringComparer.CurrentCultureIgnoreCase)
                : playlist.Entries
                    .OrderBy(
                        entry => entry.DisplayName,
                        StringComparer.CurrentCultureIgnoreCase))
            .ThenBy(entry => entry.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _sortNameDescending = !_sortNameDescending;
        NameSortButton.Content = _sortNameDescending ? "Я–А" : "А–Я";
        RefreshPlaylistEntries();
        QueuePreview();
    }

    private void SortPlaylistEntriesByDate_Click(object sender, RoutedEventArgs e)
    {
        ImagePlaylist playlist = CurrentPlaylist();
        var entries = playlist.Entries
            .Select(entry => new
            {
                Entry = entry,
                Timestamp = TryGetPlaylistTimestamp(entry)
            });
        playlist.Entries = (_sortOldestFirst
                ? entries
                    .OrderBy(item => item.Timestamp is null)
                    .ThenBy(item => item.Timestamp)
                : entries
                    .OrderBy(item => item.Timestamp is null)
                    .ThenByDescending(item => item.Timestamp))
            .ThenBy(
                item => item.Entry.DisplayName,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Entry)
            .ToList();
        _sortOldestFirst = !_sortOldestFirst;
        DateSortButton.Content = _sortOldestFirst ? "СТАРЕЕ" : "НОВЕЕ";
        RefreshPlaylistEntries();
        QueuePreview();
    }

    private static DateTime? TryGetPlaylistTimestamp(ImagePlaylistEntry entry)
    {
        try
        {
            return File.Exists(entry.Path)
                ? File.GetLastWriteTimeUtc(entry.Path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void ShufflePlaylistEntries_Click(object sender, RoutedEventArgs e)
    {
        List<ImagePlaylistEntry> entries = CurrentPlaylist().Entries;
        for (int index = entries.Count - 1; index > 0; index--)
        {
            int swapIndex = Random.Shared.Next(index + 1);
            (entries[index], entries[swapIndex]) = (entries[swapIndex], entries[index]);
        }
        RefreshPlaylistEntries();
        QueuePreview();
    }

    private void DeleteSelectedPlaylistEntries_Click(object sender, RoutedEventArgs e)
    {
        HashSet<ImagePlaylistEntry> selected = SelectedPlaylistEntries().ToHashSet();
        if (selected.Count == 0)
            return;
        CurrentPlaylist().Entries.RemoveAll(selected.Contains);
        RefreshPlaylistEntries();
        QueuePreview();
    }

    private void AddPathsToPlaylist(IEnumerable<string> sourcePaths)
    {
        List<string> expanded = ImagePlaylistCatalog.ExpandPaths(sourcePaths);
        ImagePlaylist playlist = CurrentPlaylist();
        HashSet<string> existing = playlist.Entries
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (string path in expanded)
        {
            if (!existing.Add(path))
                continue;
            playlist.Entries.Add(new ImagePlaylistEntry { Path = path });
            added++;
        }

        if (added == 0)
        {
            StatusText.Text = "ПЛЕЙЛИСТ НЕ ИЗМЕНЁН // ПОДДЕРЖИВАЕМЫЕ НОВЫЕ ФАЙЛЫ НЕ НАЙДЕНЫ";
            return;
        }

        ImageModeCheck.IsChecked = true;
        RefreshPlaylistEntries();
        QueuePreview();
        StatusText.Text = $"ДОБАВЛЕНО ОБРАЗОВ: {added} // ПЛЕЙЛИСТ В ПРЕДПРОСМОТРЕ";
    }

    private void UpdatePlaylistEntryStates(PlaylistEntryStateOperation operation)
    {
        HashSet<ImagePlaylistEntry> selected = SelectedPlaylistEntries().ToHashSet();
        if (selected.Count == 0)
            return;
        foreach (ImagePlaylistEntry entry in CurrentPlaylist().Entries)
        {
            bool isSelected = selected.Contains(entry);
            entry.Enabled = operation switch
            {
                PlaylistEntryStateOperation.EnableSelected when isSelected => true,
                PlaylistEntryStateOperation.DisableSelected when isSelected => false,
                _ => entry.Enabled
            };
        }
        PlaylistList.Items.Refresh();
        QueuePreview();
    }

    private IReadOnlyList<ImagePlaylistEntry> SelectedPlaylistEntries() =>
        PlaylistList.SelectedItems
            .OfType<ImagePlaylistEntry>()
            .ToArray();

    private ImagePlaylist CurrentPlaylist()
    {
        ImagePlaylist? playlist = _playlists.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id,
                _activePlaylistId,
                StringComparison.OrdinalIgnoreCase));
        if (playlist is not null)
            return playlist;

        if (_playlists.Count == 0)
            _playlists.Add(new ImagePlaylist());
        _activePlaylistId = _playlists[0].Id;
        return _playlists[0];
    }

    private void RefreshPlaylistUi()
    {
        CurrentPlaylist();
        RefreshPlaylistSelector();
        RefreshPlaylistEditor();
    }

    private void RefreshPlaylistSelector()
    {
        bool wasLoading = _loading;
        _loading = true;
        PlaylistCombo.Items.Clear();
        foreach (ImagePlaylist playlist in _playlists)
        {
            PlaylistCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(playlist.Name)
                    ? "Плейлист без имени"
                    : playlist.Name,
                Tag = playlist.Id
            });
        }
        SelectByTag(PlaylistCombo, _activePlaylistId);
        _loading = wasLoading;
    }

    private void RefreshPlaylistEditor()
    {
        bool wasLoading = _loading;
        _loading = true;
        ImagePlaylist playlist = CurrentPlaylist();
        PlaylistNameTextBox.Text = playlist.Name;
        PlaylistFillHorizontalCheck.IsChecked =
            playlist.Placement.FillHorizontal;
        PlaylistFillVerticalCheck.IsChecked =
            playlist.Placement.FillVertical;
        RefreshPlaylistEntries();
        _loading = wasLoading;
    }

    private void RefreshPlaylistEntries()
    {
        foreach (ImagePlaylistEntry entry in CurrentPlaylist().Entries)
            entry.RefreshAvailability();
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = CurrentPlaylist().Entries;
    }

    private static bool HasAvailablePlaylistImages(AppSettings settings) =>
        settings.ActiveImagePlaylist().Entries.Any(entry =>
            entry.Enabled
            && File.Exists(entry.Path)
            && ImagePlaylistCatalog.IsSupportedImage(entry.Path));

    private void HideButton_Click(object sender, RoutedEventArgs e) => DiscardPreviewAndHide();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DiscardPreview();

    private void PauseWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        SetPauseState(!_wallpaperPaused);
        PauseRequested?.Invoke(_wallpaperPaused);
        StatusText.Text = _wallpaperPaused
            ? "ПОТОК ОСТАНОВЛЕН // СЛОЙ КОДА СКРЫТ"
            : "ПОТОК ВОЗОБНОВЛЁН";
    }

    private void TestAttackButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        AttackRequested?.Invoke();
        StatusText.Text =
            "АТАКА СИСТЕМЫ // ПОДГОТОВКА ПЕРЕХОДА";
    }

    private void ResetParameterButton_Click(object sender, RoutedEventArgs e)
    {
        string key = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        AppSettings standard = OperatorDefaults.Create();
        _loading = true;
        switch (key)
        {
            case "SpeedMin":
                _speedMinValue = standard.SpeedMin;
                SpeedMinSlider.Value = standard.SpeedMin;
                break;
            case "SpeedMax":
                _speedMaxValue = standard.SpeedMax;
                SpeedMaxSlider.Value = standard.SpeedMax;
                break;
            case "Density": DensitySlider.Value = standard.Density; break;
            case "TrailMin": TrailMinSlider.Value = standard.TrailLengthMin; break;
            case "TrailMax": TrailMaxSlider.Value = standard.TrailLengthMax; break;
            case "MemoryMin":
                _memoryMinValue = standard.MemoryDurationMin;
                MemoryMinSlider.Value = Math.Clamp(
                    _memoryMinValue,
                    MemoryMinSlider.Minimum,
                    MemoryMinSlider.Maximum);
                break;
            case "MemoryMax":
                _memoryMaxValue = standard.MemoryDurationMax;
                MemoryMaxSlider.Value = Math.Clamp(
                    _memoryMaxValue,
                    MemoryMaxSlider.Minimum,
                    MemoryMaxSlider.Maximum);
                break;
            case "SignalMin": SignalMinSlider.Value = standard.SignalStrengthMin; break;
            case "SignalMax": SignalMaxSlider.Value = standard.SignalStrengthMax; break;
            case "SignalGlowKeys": SignalGlowKeysSlider.Value = standard.SignalGlowKeys; break;
            case "SignalGlowPriority":
                SignalGlowPrioritySlider.Value = standard.SignalGlowPriority;
                break;
            case "HeadBrightness": HeadBrightnessSlider.Value = standard.HeadBrightness; break;
            case "HeadGlow": HeadGlowSlider.Value = standard.HeadGlow; break;
            case "HeadImpulseDecay": HeadImpulseDecaySlider.Value = standard.HeadImpulseDecay; break;
            case "HeadImpulseProbability":
                HeadImpulseProbabilitySlider.Value = standard.HeadImpulseProbability;
                break;
            case "HeadWeight": HeadWeightSlider.Value = standard.HeadWeight; break;
            case "Interception": InterceptionSlider.Value = standard.InterceptionRate; break;
            case "StreamLifetimeMin":
                StreamLifetimeMinSlider.Value = standard.StreamLifetimeMin;
                break;
            case "StreamLifetimeMax":
                StreamLifetimeMaxSlider.Value = standard.StreamLifetimeMax;
                break;
            case "FontSize": FontSizeSlider.Value = standard.FontSize; break;
            case "GlyphStretch": GlyphStretchSlider.Value = standard.GlyphStretch; break;
            case "GlyphWeight": GlyphWeightSlider.Value = standard.GlyphWeight; break;
            case "SignalHue": SignalHueSlider.Value = standard.SignalHue; break;
            case "SignalBrightness":
                SignalBrightnessSlider.Value = standard.SignalBrightness;
                break;
            case "BackgroundHue":
                BackgroundHueSlider.Value = standard.BackgroundHue;
                break;
            case "BackgroundBrightness":
                BackgroundBrightnessSlider.Value = standard.BackgroundBrightness;
                break;
            case "Duration":
                _imageDurationSecondsValue = standard.ImageDurationSeconds;
                DurationSlider.Value = standard.ImageDurationSeconds;
                break;
            case "ImageExpressiveness": ImageExpressivenessSlider.Value = standard.ImageExpressiveness; break;
            case "ImageGlyphMatch": ImageGlyphMatchSlider.Value = standard.ImageGlyphMatch; break;
            case "ImageStability": ImageStabilitySlider.Value = standard.ImageStability; break;
            case "ImageResistance": ImageResistanceSlider.Value = standard.ImageResistance; break;
            case "ImageBrightness": ImageBrightnessSlider.Value = standard.ImageBrightness; break;
            case "ImageLocalContrast": ImageLocalContrastSlider.Value = standard.ImageLocalContrast; break;
            case "ImageDetailStrength": ImageDetailStrengthSlider.Value = standard.ImageDetailStrength; break;
            case "ImageEdgeStrength": ImageEdgeStrengthSlider.Value = standard.ImageEdgeStrength; break;
            case "ImageShadowBalance": ImageShadowBalanceSlider.Value = standard.ImageShadowBalance; break;
            case "ImagePaletteAdaptation": ImagePaletteAdaptationSlider.Value = standard.ImagePaletteAdaptation; break;
            case "ImageToneCalmness": ImageToneCalmnessSlider.Value = standard.ImageToneCalmness; break;
            case "FontFamily":
                EnsureFontOption(standard.FontFamily);
                SelectByTag(FontCombo, standard.FontFamily);
                break;
            case "ImagePreparationMode": SelectByTag(ImagePreparationModeCombo, standard.ImagePreparationMode); break;
            case "ImageStructureMode": SelectByTag(ImageStructureModeCombo, standard.ImageStructureMode); break;
            case "FramesPerSecond": SelectByTag(FpsCombo, standard.FramesPerSecond.ToString()); break;
            case "ImageMode": ImageModeCheck.IsChecked = standard.ImageMode; break;
            case "StartWithWindows": AutostartCheck.IsChecked = standard.StartWithWindows; break;
            case "PauseDuringFullscreenApps":
                PauseDuringFullscreenAppsCheck.IsChecked = standard.PauseDuringFullscreenApps;
                break;
            case "AttackIdleMinutes":
                _attackIdleMinutesValue =
                    standard.AttackIdleMinutes;
                AttackIdleMinutesSlider.Value =
                    standard.AttackIdleMinutes;
                break;
            case "AttackTransitionSeconds":
                _attackTransitionSecondsValue =
                    standard.AttackTransitionSeconds;
                AttackTransitionSecondsSlider.Value =
                    standard.AttackTransitionSeconds;
                break;
            case "VirtualOutputWidth":
                VirtualOutputWidthSlider.Value =
                    standard.VirtualOutputWidth;
                break;
            case "VirtualOutputHeight":
                VirtualOutputHeightSlider.Value =
                    standard.VirtualOutputHeight;
                break;
            case "CurrentContour":
                ResetCurrentContour(standard);
                break;
            case "AnalysisBlock":
                ResetAnalysisBlock(standard);
                break;
            case "OutputBlock":
                ResetOutputBlock(standard);
                break;
            case "DatabaseBlock":
                ResetSourcesBlock(standard);
                ResetAnalysisBlock(standard);
                ResetOutputBlock(standard);
                break;
            case "AttackBlock":
                ResetAttackBlock(standard);
                break;
            case "SystemBlock":
                ResetSystemBlock(standard);
                break;
            case "CurveCharacter":
                AdjustmentForSelectedCurve().Character = 0;
                break;
            case "CurveHorizontalShift":
                AdjustmentForSelectedCurve().HorizontalShift = 0;
                break;
            case "CurveVerticalShift":
                AdjustmentForSelectedCurve().VerticalShift = 0;
                break;
            case "CurrentCurve":
                {
                    string kind = SelectedTag(CurveKindCombo, FlowCurveProfiles.SpeedKind);
                    IReadOnlyList<CurvePoint> curve = kind switch
                    {
                        FlowCurveProfiles.SpeedKind => standard.SpeedCurve,
                        FlowCurveProfiles.LengthKind => standard.TrailLengthCurve,
                        FlowCurveProfiles.SignalKind => standard.SignalCurve,
                        FlowCurveProfiles.FilterKind => standard.StreamFilterCurve,
                        FlowCurveProfiles.MemoryKind => standard.MemoryCurve,
                        _ => FlowCurveProfiles.DefaultHeadPulse()
                    };
                    SetCurve(kind, curve);
                    SetAdjustment(
                        kind,
                        kind switch
                        {
                            FlowCurveProfiles.SpeedKind =>
                                standard.SpeedCurveAdjustment,
                            FlowCurveProfiles.LengthKind =>
                                standard.TrailLengthCurveAdjustment,
                            FlowCurveProfiles.SignalKind =>
                                standard.SignalCurveAdjustment,
                            FlowCurveProfiles.FilterKind =>
                                standard.StreamFilterCurveAdjustment,
                            FlowCurveProfiles.MemoryKind =>
                                standard.MemoryCurveAdjustment,
                            _ => new CurveAdjustment()
                        });
                    break;
                }
        }
        if (key == "SpeedMin" && _speedMinValue > _speedMaxValue)
        {
            _speedMaxValue = _speedMinValue;
            SpeedMaxSlider.Value = Math.Clamp(
                _speedMaxValue,
                SpeedMaxSlider.Minimum,
                SpeedMaxSlider.Maximum);
        }
        else if (key == "SpeedMax" && _speedMaxValue < _speedMinValue)
        {
            _speedMinValue = _speedMaxValue;
            SpeedMinSlider.Value = Math.Clamp(
                _speedMinValue,
                SpeedMinSlider.Minimum,
                SpeedMinSlider.Maximum);
        }
        else if (key == "TrailMin" && TrailMinSlider.Value > TrailMaxSlider.Value)
            TrailMaxSlider.Value = TrailMinSlider.Value;
        else if (key == "TrailMax" && TrailMaxSlider.Value < TrailMinSlider.Value)
            TrailMinSlider.Value = TrailMaxSlider.Value;
        else if (key == "MemoryMin"
                 && _memoryMinValue > _memoryMaxValue)
        {
            _memoryMaxValue = _memoryMinValue;
            MemoryMaxSlider.Value = Math.Clamp(
                _memoryMaxValue,
                MemoryMaxSlider.Minimum,
                MemoryMaxSlider.Maximum);
        }
        else if (key == "MemoryMax"
                 && _memoryMaxValue < _memoryMinValue)
        {
            _memoryMinValue = _memoryMaxValue;
            MemoryMinSlider.Value = Math.Clamp(
                _memoryMinValue,
                MemoryMinSlider.Minimum,
                MemoryMinSlider.Maximum);
        }
        else if (key == "SignalMin"
                 && SignalMinSlider.Value > SignalMaxSlider.Value)
        {
            SignalMaxSlider.Value = SignalMinSlider.Value;
        }
        else if (key == "SignalMax"
                 && SignalMaxSlider.Value < SignalMinSlider.Value)
        {
            SignalMinSlider.Value = SignalMaxSlider.Value;
        }
        else if (key == "StreamLifetimeMin"
                 && StreamLifetimeMinSlider.Value > StreamLifetimeMaxSlider.Value)
        {
            StreamLifetimeMaxSlider.Value = StreamLifetimeMinSlider.Value;
        }
        else if (key == "StreamLifetimeMax"
                 && StreamLifetimeMaxSlider.Value < StreamLifetimeMinSlider.Value)
        {
            StreamLifetimeMinSlider.Value = StreamLifetimeMaxSlider.Value;
        }
        RefreshCurveEditor();
        UpdateImagePreparationUi();
        UpdateCollapsibleSections();
        RefreshLabels(force: true);
        _loading = false;
        bool terminalContourReset = key == "CurrentContour"
            && SelectedTag(
                CurveKindCombo,
                FlowCurveProfiles.TerminalKind) == FlowCurveProfiles.TerminalKind;
        if (key is "FontSize" or "GlyphStretch" or "GlyphWeight"
            || terminalContourReset)
            QueueFontPreview();
        else
            QueuePreview();
        e.Handled = true;
    }

    private void ResetCurrentContour(AppSettings standard)
    {
        string kind = SelectedTag(
            CurveKindCombo,
            FlowCurveProfiles.TerminalKind);
        if (kind == FlowCurveProfiles.TerminalKind)
        {
            EnsureFontOption(standard.FontFamily);
            SelectByTag(FontCombo, standard.FontFamily);
            FontSizeSlider.Value = standard.FontSize;
            GlyphStretchSlider.Value = standard.GlyphStretch;
            GlyphWeightSlider.Value = standard.GlyphWeight;
            SignalHueSlider.Value = standard.SignalHue;
            SignalBrightnessSlider.Value = standard.SignalBrightness;
            BackgroundHueSlider.Value = standard.BackgroundHue;
            BackgroundBrightnessSlider.Value = standard.BackgroundBrightness;
            return;
        }

        if (kind == FlowCurveProfiles.SpeedKind)
        {
            _speedMinValue = standard.SpeedMin;
            _speedMaxValue = standard.SpeedMax;
            SpeedMinSlider.Value = standard.SpeedMin;
            SpeedMaxSlider.Value = standard.SpeedMax;
            SetCurve(kind, standard.SpeedCurve);
            SetAdjustment(kind, standard.SpeedCurveAdjustment);
        }
        else if (kind == FlowCurveProfiles.LengthKind)
        {
            TrailMinSlider.Value = standard.TrailLengthMin;
            TrailMaxSlider.Value = standard.TrailLengthMax;
            DensitySlider.Value = standard.Density;
            InterceptionSlider.Value = standard.InterceptionRate;
            SetCurve(kind, standard.TrailLengthCurve);
            SetAdjustment(kind, standard.TrailLengthCurveAdjustment);
        }
        else if (kind == FlowCurveProfiles.SignalKind)
        {
            SignalMinSlider.Value = standard.SignalStrengthMin;
            SignalMaxSlider.Value = standard.SignalStrengthMax;
            SignalGlowKeysSlider.Value = standard.SignalGlowKeys;
            SignalGlowPrioritySlider.Value = standard.SignalGlowPriority;
            SetCurve(kind, standard.SignalCurve);
            SetAdjustment(kind, standard.SignalCurveAdjustment);
        }
        else if (kind == FlowCurveProfiles.FilterKind)
        {
            StreamLifetimeMinSlider.Value = standard.StreamLifetimeMin;
            StreamLifetimeMaxSlider.Value = standard.StreamLifetimeMax;
            SetCurve(kind, standard.StreamFilterCurve);
            SetAdjustment(kind, standard.StreamFilterCurveAdjustment);
        }
        else if (kind == FlowCurveProfiles.MemoryKind)
        {
            _memoryMinValue = standard.MemoryDurationMin;
            _memoryMaxValue = standard.MemoryDurationMax;
            MemoryMinSlider.Value = Math.Clamp(
                _memoryMinValue,
                MemoryMinSlider.Minimum,
                MemoryMinSlider.Maximum);
            MemoryMaxSlider.Value = Math.Clamp(
                _memoryMaxValue,
                MemoryMaxSlider.Minimum,
                MemoryMaxSlider.Maximum);
            SetCurve(kind, standard.MemoryCurve);
            SetAdjustment(kind, standard.MemoryCurveAdjustment);
        }
        else if (kind == FlowCurveProfiles.HeadPulseKind)
        {
            HeadBrightnessSlider.Value = standard.HeadBrightness;
            HeadWeightSlider.Value = standard.HeadWeight;
            HeadGlowSlider.Value = standard.HeadGlow;
            HeadImpulseDecaySlider.Value = standard.HeadImpulseDecay;
            HeadImpulseProbabilitySlider.Value = standard.HeadImpulseProbability;
        }
    }

    private void ResetSourcesBlock(AppSettings standard)
    {
        _imageDurationSecondsValue = standard.ImageDurationSeconds;
        DurationSlider.Value = standard.ImageDurationSeconds;
    }

    private void ResetAnalysisBlock(AppSettings standard)
    {
        SelectByTag(ImagePreparationModeCombo, standard.ImagePreparationMode);
        ImageLocalContrastSlider.Value = standard.ImageLocalContrast;
        ImageDetailStrengthSlider.Value = standard.ImageDetailStrength;
        ImageEdgeStrengthSlider.Value = standard.ImageEdgeStrength;
        ImageShadowBalanceSlider.Value = standard.ImageShadowBalance;
        ImagePaletteAdaptationSlider.Value = standard.ImagePaletteAdaptation;
        ImageToneCalmnessSlider.Value = standard.ImageToneCalmness;
        SelectByTag(ImageStructureModeCombo, standard.ImageStructureMode);
    }

    private void ResetOutputBlock(AppSettings standard)
    {
        ImageGlyphMatchSlider.Value = standard.ImageGlyphMatch;
        ImageStabilitySlider.Value = standard.ImageStability;
        ImageResistanceSlider.Value = standard.ImageResistance;
        ImageBrightnessSlider.Value = standard.ImageBrightness;
        ImageExpressivenessSlider.Value = standard.ImageExpressiveness;
    }

    private void ResetSystemBlock(AppSettings standard)
    {
        SelectByTag(FpsCombo, standard.FramesPerSecond.ToString());
        PauseDuringFullscreenAppsCheck.IsChecked =
            standard.PauseDuringFullscreenApps;
        AutostartCheck.IsChecked = standard.StartWithWindows;
    }

    private void ResetAttackBlock(AppSettings standard)
    {
        _attackIdleMinutesValue = standard.AttackIdleMinutes;
        AttackIdleMinutesSlider.Value =
            standard.AttackIdleMinutes;
        _attackTransitionSecondsValue =
            standard.AttackTransitionSeconds;
        AttackTransitionSecondsSlider.Value =
            standard.AttackTransitionSeconds;
    }

    private void AuthorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText("Gloynus@gmail.com");
            StatusText.Text = _hasPendingChanges
                ? "КОНТАКТ СКОПИРОВАН // ЕСТЬ НЕПРИМЕНЁННЫЕ ИЗМЕНЕНИЯ"
                : "КОНТАКТ АВТОРА СКОПИРОВАН // GLOYNUS@GMAIL.COM";
        }
        catch
        {
            StatusText.Text = "НЕ УДАЛОСЬ ПЕРЕДАТЬ КОНТАКТ В БУФЕР";
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CurveKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurveEditor is null || CurvePresetCombo is null)
            return;
        bool wasLoading = _loading;
        _loading = true;
        RefreshCurveEditor();
        _loading = wasLoading;
        e.Handled = true;
    }

    private void CurveKindTabs_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin)
            return;
        ListBoxItem? item = FindAncestor<ListBoxItem>(origin);
        if (item is null)
            return;
        CurveKindCombo.SelectedItem = item;
        e.Handled = true;
    }

    private void CurvePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CurveEditor is null)
            return;
        string preset = SelectedTag(CurvePresetCombo, "Custom");
        if (preset == "Custom")
        {
            e.Handled = true;
            return;
        }

        string kind = SelectedTag(CurveKindCombo, FlowCurveProfiles.LengthKind);
        if (kind == FlowCurveProfiles.TerminalKind)
        {
            e.Handled = true;
            return;
        }
        List<CurvePoint> curve = FlowCurveProfiles.Create(kind, preset);
        SetCurve(kind, curve);
        SetAdjustment(kind, new CurveAdjustment());
        RefreshCurveEditor();
        QueuePreview();
        e.Handled = true;
    }

    private void CurveEditor_CurveChanged(object? sender, EventArgs e)
    {
        if (_loading)
            return;
        string kind = SelectedTag(CurveKindCombo, FlowCurveProfiles.LengthKind);
        if (kind == FlowCurveProfiles.TerminalKind)
            return;
        SetCurve(kind, CurveEditor.CopyCurve());
        SetAdjustment(kind, new CurveAdjustment());
        _loading = true;
        LoadCurveAdjustmentControls(kind);
        SelectByTag(CurvePresetCombo, "Custom");
        _loading = false;
        QueuePreview();
    }

    private void CurveModifierSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading
            || CurveEditor is null
            || CurveCharacterSlider is null
            || CurveHorizontalShiftSlider is null
            || CurveVerticalShiftSlider is null)
        {
            return;
        }

        string kind = SelectedTag(CurveKindCombo, FlowCurveProfiles.SpeedKind);
        if (kind is FlowCurveProfiles.HeadPulseKind or FlowCurveProfiles.TerminalKind)
            return;
        CurveAdjustment adjustment = AdjustmentFor(kind);
        adjustment.Character = CurveCharacterSlider.Value;
        adjustment.HorizontalShift = CurveHorizontalShiftSlider.Value;
        adjustment.VerticalShift = CurveVerticalShiftSlider.Value;
        IReadOnlyList<CurvePoint> effective = EffectiveCurveFor(kind);
        CurveEditor.SetCurve(kind, effective);
        QueuePreview();
    }

    private void ImagePreparationModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ImagePreparationCustomPanel is null)
            return;
        UpdateImagePreparationUi();
        if (!_loading)
            QueuePreview();
        e.Handled = true;
    }

    private void AnySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        if (ReferenceEquals(sender, DurationSlider))
            _imageDurationSecondsValue = DurationSlider.Value;
        else if (ReferenceEquals(sender, AttackIdleMinutesSlider))
            _attackIdleMinutesValue =
                AttackIdleMinutesSlider.Value;
        else if (ReferenceEquals(
            sender,
            AttackTransitionSecondsSlider))
        {
            _attackTransitionSecondsValue =
                AttackTransitionSecondsSlider.Value;
        }
        _loading = true;
        RefreshLabels();
        _loading = false;
        if (ReferenceEquals(sender, FontSizeSlider)
            || ReferenceEquals(sender, GlyphStretchSlider)
            || ReferenceEquals(sender, GlyphWeightSlider))
        {
            QueueFontPreview();
        }
        else if (ReferenceEquals(sender, SignalHueSlider)
                 || ReferenceEquals(sender, SignalBrightnessSlider)
                 || ReferenceEquals(sender, BackgroundHueSlider)
                 || ReferenceEquals(sender, BackgroundBrightnessSlider))
        {
            QueueColorPreview();
        }
        else
        {
            QueuePreview();
        }
    }

    private void TrailRangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TrailMinSlider is null || TrailMaxSlider is null)
            return;

        _loading = true;
        if (ReferenceEquals(sender, TrailMinSlider) && TrailMinSlider.Value > TrailMaxSlider.Value)
            TrailMaxSlider.Value = TrailMinSlider.Value;
        else if (ReferenceEquals(sender, TrailMaxSlider) && TrailMaxSlider.Value < TrailMinSlider.Value)
            TrailMinSlider.Value = TrailMaxSlider.Value;
        RefreshLabels();
        _loading = false;
        QueuePreview();
    }

    private void MemoryRangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || MemoryMinSlider is null || MemoryMaxSlider is null)
            return;

        _loading = true;
        if (ReferenceEquals(sender, MemoryMinSlider))
        {
            _memoryMinValue = MemoryMinSlider.Value;
            if (_memoryMinValue > _memoryMaxValue)
            {
                _memoryMaxValue = _memoryMinValue;
                MemoryMaxSlider.Value = Math.Clamp(
                    _memoryMaxValue,
                    MemoryMaxSlider.Minimum,
                    MemoryMaxSlider.Maximum);
            }
        }
        else if (ReferenceEquals(sender, MemoryMaxSlider))
        {
            _memoryMaxValue = MemoryMaxSlider.Value;
            if (_memoryMaxValue < _memoryMinValue)
            {
                _memoryMinValue = _memoryMaxValue;
                MemoryMinSlider.Value = Math.Clamp(
                    _memoryMinValue,
                    MemoryMinSlider.Minimum,
                    MemoryMinSlider.Maximum);
            }
        }
        RefreshLabels();
        _loading = false;
        QueuePreview();
    }

    private void SignalRangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SignalMinSlider is null || SignalMaxSlider is null)
            return;

        _loading = true;
        if (ReferenceEquals(sender, SignalMinSlider)
            && SignalMinSlider.Value > SignalMaxSlider.Value)
        {
            SignalMaxSlider.Value = SignalMinSlider.Value;
        }
        else if (ReferenceEquals(sender, SignalMaxSlider)
                 && SignalMaxSlider.Value < SignalMinSlider.Value)
        {
            SignalMinSlider.Value = SignalMaxSlider.Value;
        }
        RefreshLabels();
        _loading = false;
        QueuePreview();
    }

    private void SpeedRangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SpeedMinSlider is null || SpeedMaxSlider is null)
            return;

        _loading = true;
        if (ReferenceEquals(sender, SpeedMinSlider))
        {
            _speedMinValue = SpeedMinSlider.Value;
            if (_speedMinValue > _speedMaxValue)
            {
                _speedMaxValue = _speedMinValue;
                SpeedMaxSlider.Value = _speedMaxValue;
            }
        }
        else if (ReferenceEquals(sender, SpeedMaxSlider))
        {
            _speedMaxValue = SpeedMaxSlider.Value;
            if (_speedMaxValue < _speedMinValue)
            {
                _speedMinValue = _speedMaxValue;
                SpeedMinSlider.Value = _speedMinValue;
            }
        }
        RefreshLabels();
        _loading = false;
        QueuePreview();
    }

    private void StreamLifetimeRangeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading
            || StreamLifetimeMinSlider is null
            || StreamLifetimeMaxSlider is null)
        {
            return;
        }

        _loading = true;
        if (ReferenceEquals(sender, StreamLifetimeMinSlider)
            && StreamLifetimeMinSlider.Value > StreamLifetimeMaxSlider.Value)
        {
            StreamLifetimeMaxSlider.Value = StreamLifetimeMinSlider.Value;
        }
        else if (ReferenceEquals(sender, StreamLifetimeMaxSlider)
                 && StreamLifetimeMaxSlider.Value < StreamLifetimeMinSlider.Value)
        {
            StreamLifetimeMinSlider.Value = StreamLifetimeMaxSlider.Value;
        }
        RefreshLabels();
        _loading = false;
        QueuePreview();
    }

    private void AnyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || e.OriginalSource is not TextBox input)
            return;

        NumericInputSpec? spec = GetNumericInputSpec(input);
        double displayed = 0;
        bool validNumber = false;
        if (spec is NumericInputSpec numericSpec
            && TryParseNumber(input.Text, out displayed))
        {
            validNumber = true;
            SetNumericValue(
                input,
                numericSpec,
                displayed / numericSpec.DisplayScale,
                useManualLimits: true);
            RefreshColorSwatches();
            RefreshTerminalPreview();
        }
        string? parameter = input.Tag?.ToString();
        if (parameter is "FontSize" or "GlyphStretch" or "GlyphWeight")
        {
            if (validNumber)
                QueueFontPreview();
            else
                UpdateDraftStatus();
            return;
        }
        if (parameter is "SignalHue"
            or "SignalBrightness"
            or "BackgroundHue"
            or "BackgroundBrightness")
        {
            if (validNumber)
                QueueColorPreview();
            else
                UpdateDraftStatus();
            return;
        }
        QueuePreview();
    }

    private void AnySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshTerminalPreview();
        if (!_loading)
            QueuePreview();
    }

    private void AnyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (e.Source is ToggleButton toggle
            && IsSectionBookmark(toggle))
        {
            return;
        }
        UpdateCollapsibleSections();
        if (!_loading)
            QueuePreview();
    }

    private void QueuePreview()
    {
        UpdateDraftStatus();
        _colorPreviewTimer.Stop();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void QueueFontPreview()
    {
        _colorPreviewTimer.Stop();
        UpdateDraftStatus();
        StatusText.Text = _hasPendingChanges
            ? "МАСШТАБ ЗАФИКСИРУЕТСЯ ПОСЛЕ ПАУЗЫ // ЕСТЬ НЕПРИМЕНЁННЫЕ ИЗМЕНЕНИЯ"
            : "ПАРАМЕТРЫ СОВПАДАЮТ // ПОТОК СИНХРОНИЗИРОВАН";
        _fontPreviewTimer.Stop();
        _fontPreviewTimer.Start();
    }

    private void QueueColorPreview()
    {
        UpdateDraftStatus();
        _previewTimer.Stop();
        if (!_colorPreviewTimer.IsEnabled)
            _colorPreviewTimer.Start();
    }

    private void UpdateDraftStatus()
    {
        AppSettings preview = ReadSettingsFromControls();
        _hasPendingChanges = HasInvalidNumericInput()
            || !AppSettingsComparer.Equivalent(preview, _source);
        UpdateFooterButtons();
        StatusText.Text = _hasPendingChanges
            ? "ПРЕДПРОСМОТР В ПОТОКЕ // ЕСТЬ НЕПРИМЕНЁННЫЕ ИЗМЕНЕНИЯ"
            : "ПАРАМЕТРЫ СОВПАДАЮТ // ПОТОК СИНХРОНИЗИРОВАН";
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        SettingsPreviewed?.Invoke(ReadLivePreviewSettings());
    }

    private void ColorPreviewTimer_Tick(object? sender, EventArgs e)
    {
        _colorPreviewTimer.Stop();
        if (!HasInvalidNumericInput())
            SettingsPreviewed?.Invoke(ReadLivePreviewSettings());
    }

    private void FontPreviewTimer_Tick(object? sender, EventArgs e)
    {
        _fontPreviewTimer.Stop();
        if (HasInvalidNumericInput())
            return;
        _previewTimer.Stop();
        _colorPreviewTimer.Stop();
        _livePreviewFontSize = FontSizeSlider.Value;
        _livePreviewGlyphStretch = GlyphStretchSlider.Value;
        _livePreviewGlyphWeight = GlyphWeightSlider.Value;
        SettingsPreviewed?.Invoke(ReadSettingsFromControls());
    }

    private AppSettings ReadLivePreviewSettings()
    {
        AppSettings preview = ReadSettingsFromControls();
        if (_fontPreviewTimer.IsEnabled)
        {
            AppSettings display = SelectedMonitorSettings(preview);
            display.FontSize = _livePreviewFontSize;
            display.GlyphStretch = _livePreviewGlyphStretch;
            display.GlyphWeight = _livePreviewGlyphWeight;
            SynchronizeLegacySettings(preview);
        }
        return preview;
    }

    private void DiscardPreview()
    {
        _virtualResolutionTimer.Stop();
        _previewTimer.Stop();
        _fontPreviewTimer.Stop();
        _colorPreviewTimer.Stop();
        if (_hasPendingChanges)
            SettingsPreviewed?.Invoke(_source.Copy());
        LoadSettings(_source);
    }

    private void DiscardPreviewAndHide()
    {
        DiscardPreview();
        Hide();
    }

    private void SetSynchronizedStatus() =>
        StatusText.Text = "СИСТЕМА В СЕТИ // ПАРАМЕТРЫ СИНХРОНИЗИРОВАНЫ";

    private void UpdateFooterButtons()
    {
        if (CancelButton is null || ApplyButton is null)
            return;
        CancelButton.Visibility = _hasPendingChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelButton.ToolTip = "Вернуть последние применённые параметры";
        ApplyButton.Content = _hasPendingChanges ? "ПРИМЕНИТЬ" : "СКРЫТЬ";
        ApplyButton.ToolTip = _hasPendingChanges
            ? "Сохранить и применить все изменения"
            : "Скрыть консоль; обои продолжат работать в фоне";
        ApplyButton.Background = new SolidColorBrush(
            _hasPendingChanges
                ? System.Windows.Media.Color.FromRgb(14, 107, 50)
                : System.Windows.Media.Color.FromRgb(11, 43, 24));
        UpdatePresetActionButtons();
    }

    private void LoadDraft(AppSettings draft)
    {
        AppSettings applied = _source.Copy();
        LoadSettings(draft);
        _source = applied;
        QueuePreview();
    }

    private bool HasInvalidNumericInput()
    {
        foreach (TextBox input in NumericInputs())
        {
            if (!TryParseNumber(input.Text, out _))
                return true;
        }
        return false;
    }

    private IEnumerable<TextBox> NumericInputs()
    {
        yield return CalibrationSpeedMinInput;
        yield return CalibrationSpeedMaxInput;
        yield return CalibrationDensityInput;
        yield return CalibrationTrailMinInput;
        yield return CalibrationTrailMaxInput;
        yield return CalibrationMemoryMinInput;
        yield return CalibrationMemoryMaxInput;
        yield return CalibrationSignalMinInput;
        yield return CalibrationSignalMaxInput;
        yield return CalibrationSignalGlowKeysInput;
        yield return CalibrationSignalGlowPriorityInput;
        yield return CalibrationHeadBrightnessInput;
        yield return CalibrationHeadGlowInput;
        yield return CalibrationHeadImpulseDecayInput;
        yield return CalibrationHeadImpulseProbabilityInput;
        yield return CalibrationHeadWeightInput;
        yield return CalibrationInterceptionInput;
        yield return CalibrationStreamLifetimeMinInput;
        yield return CalibrationStreamLifetimeMaxInput;
        yield return FontSizeInput;
        yield return GlyphStretchInput;
        yield return GlyphWeightInput;
        yield return SignalHueInput;
        yield return SignalBrightnessInput;
        yield return BackgroundHueInput;
        yield return BackgroundBrightnessInput;
        yield return DurationInput;
        yield return ImageExpressivenessInput;
        yield return ImageGlyphMatchInput;
        yield return ImageStabilityInput;
        yield return ImageResistanceInput;
        yield return ImageBrightnessInput;
        yield return ImageLocalContrastInput;
        yield return ImageDetailStrengthInput;
        yield return ImageEdgeStrengthInput;
        yield return ImageShadowBalanceInput;
        yield return ImagePaletteAdaptationInput;
        yield return ImageToneCalmnessInput;
        yield return AttackIdleMinutesInput;
        yield return AttackTransitionSecondsInput;
    }

    private void NumericInput_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox input || !input.IsKeyboardFocused)
            return;
        e.Handled = AdjustNumericInput(input, e.Delta > 0 ? 1 : -1);
    }

    private void NumericInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox input)
            return;

        if (e.Key == Key.Enter)
        {
            CommitNumericInput(input);
            input.SelectAll();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = AdjustNumericInput(input, e.Key == Key.Up ? 1 : -1);
        }
    }

    private void NumericInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox input)
            CommitNumericInput(input);
    }

    private bool AdjustNumericInput(TextBox input, int direction)
    {
        NumericInputSpec? spec = GetNumericInputSpec(input);
        if (spec is null)
            return false;

        string originalText = input.Text;
        int originalCaret = Math.Clamp(input.CaretIndex, 0, originalText.Length);
        int digitIndex = FindTargetDigit(originalText, originalCaret);
        if (digitIndex < 0)
            return false;

        int exponent = DigitExponent(originalText, digitIndex);
        int existingDecimals = CountFractionDigits(originalText);
        int editingDecimals = Math.Clamp(Math.Max(existingDecimals, Math.Max(0, -exponent)), 0, 8);
        double displayed = TryParseNumber(originalText, out double parsed)
            ? parsed
            : NumericValue(input, spec.Value) * spec.Value.DisplayScale;
        double step = Math.Pow(10, exponent);
        double adjusted = Math.Round(displayed + direction * step, editingDecimals, MidpointRounding.AwayFromZero);
        double value = Math.Clamp(
            adjusted / spec.Value.DisplayScale,
            spec.Value.Slider.Minimum,
            spec.Value.Slider.Maximum);
        value = NormalizeNumericValue(input, value);
        SetNumericValue(
            input,
            spec.Value,
            value,
            useManualLimits: false);
        string updatedText = FormatForEditing(value * spec.Value.DisplayScale, editingDecimals);
        input.Text = updatedText;
        input.CaretIndex = originalCaret >= originalText.Length
            ? updatedText.Length
            : FindCaretForExponent(updatedText, exponent);
        return true;
    }

    private void CommitAllNumericInputs()
    {
        CommitNumericInput(CalibrationSpeedMinInput);
        CommitNumericInput(CalibrationSpeedMaxInput);
        CommitNumericInput(CalibrationDensityInput);
        CommitNumericInput(CalibrationTrailMinInput);
        CommitNumericInput(CalibrationTrailMaxInput);
        CommitNumericInput(CalibrationMemoryMinInput);
        CommitNumericInput(CalibrationMemoryMaxInput);
        CommitNumericInput(CalibrationSignalMinInput);
        CommitNumericInput(CalibrationSignalMaxInput);
        CommitNumericInput(CalibrationSignalGlowKeysInput);
        CommitNumericInput(CalibrationSignalGlowPriorityInput);
        CommitNumericInput(CalibrationHeadBrightnessInput);
        CommitNumericInput(CalibrationHeadGlowInput);
        CommitNumericInput(CalibrationHeadImpulseDecayInput);
        CommitNumericInput(CalibrationHeadImpulseProbabilityInput);
        CommitNumericInput(CalibrationHeadWeightInput);
        CommitNumericInput(CalibrationInterceptionInput);
        CommitNumericInput(CalibrationStreamLifetimeMinInput);
        CommitNumericInput(CalibrationStreamLifetimeMaxInput);
        CommitNumericInput(FontSizeInput);
        CommitNumericInput(GlyphStretchInput);
        CommitNumericInput(GlyphWeightInput);
        CommitNumericInput(SignalHueInput);
        CommitNumericInput(SignalBrightnessInput);
        CommitNumericInput(BackgroundHueInput);
        CommitNumericInput(BackgroundBrightnessInput);
        CommitNumericInput(DurationInput);
        CommitNumericInput(ImageExpressivenessInput);
        CommitNumericInput(ImageGlyphMatchInput);
        CommitNumericInput(ImageStabilityInput);
        CommitNumericInput(ImageResistanceInput);
        CommitNumericInput(ImageBrightnessInput);
        CommitNumericInput(ImageLocalContrastInput);
        CommitNumericInput(ImageDetailStrengthInput);
        CommitNumericInput(ImageEdgeStrengthInput);
        CommitNumericInput(ImageShadowBalanceInput);
        CommitNumericInput(ImagePaletteAdaptationInput);
        CommitNumericInput(ImageToneCalmnessInput);
        CommitNumericInput(AttackIdleMinutesInput);
        CommitNumericInput(AttackTransitionSecondsInput);
    }

    private void CommitNumericInput(TextBox input)
    {
        NumericInputSpec? spec = GetNumericInputSpec(input);
        if (spec is null)
            return;

        if (TryParseNumber(input.Text, out double displayed))
        {
            SetNumericValue(
                input,
                spec.Value,
                displayed / spec.Value.DisplayScale,
                useManualLimits: true);
        }
        UpdateNumericInput(
            input,
            NumericValue(input, spec.Value) * spec.Value.DisplayScale,
            spec.Value.Format,
            force: true);
    }

    private NumericInputSpec? GetNumericInputSpec(TextBox input) => input.Tag?.ToString() switch
    {
        "SpeedMin" => new NumericInputSpec(
            SpeedMinSlider,
            100,
            "0.#",
            AppSettings.MinimumSpeed,
            AppSettings.MaximumManualSpeed),
        "SpeedMax" => new NumericInputSpec(
            SpeedMaxSlider,
            100,
            "0.#",
            AppSettings.MinimumSpeed,
            AppSettings.MaximumManualSpeed),
        "Density" => new NumericInputSpec(DensitySlider, 100, "0.#"),
        "TrailMin" => new NumericInputSpec(TrailMinSlider, 100, "0.#"),
        "TrailMax" => new NumericInputSpec(TrailMaxSlider, 100, "0.#"),
        "MemoryMin" => new NumericInputSpec(
            MemoryMinSlider,
            100,
            "0.#",
            0.0,
            TrailMemoryModel.MaximumDuration),
        "MemoryMax" => new NumericInputSpec(
            MemoryMaxSlider,
            100,
            "0.#",
            0.0,
            TrailMemoryModel.MaximumDuration),
        "SignalMin" => new NumericInputSpec(
            SignalMinSlider,
            SignalModel.MaximumLevel,
            "0"),
        "SignalMax" => new NumericInputSpec(
            SignalMaxSlider,
            SignalModel.MaximumLevel,
            "0"),
        "SignalGlowKeys" => new NumericInputSpec(
            SignalGlowKeysSlider,
            100,
            "0.#"),
        "SignalGlowPriority" => new NumericInputSpec(
            SignalGlowPrioritySlider,
            100,
            "0.#"),
        "HeadBrightness" => new NumericInputSpec(HeadBrightnessSlider, 100, "0.#"),
        "HeadGlow" => new NumericInputSpec(HeadGlowSlider, 100, "0.#"),
        "HeadImpulseDecay" => new NumericInputSpec(HeadImpulseDecaySlider, 100, "0.#"),
        "HeadImpulseProbability" => new NumericInputSpec(
            HeadImpulseProbabilitySlider,
            100,
            "0.#"),
        "HeadWeight" => new NumericInputSpec(HeadWeightSlider, 100, "0.#"),
        "Interception" => new NumericInputSpec(InterceptionSlider, 100, "0.#"),
        "StreamLifetimeMin" => new NumericInputSpec(
            StreamLifetimeMinSlider,
            100,
            "0.#"),
        "StreamLifetimeMax" => new NumericInputSpec(
            StreamLifetimeMaxSlider,
            100,
            "0.#"),
        "FontSize" => new NumericInputSpec(FontSizeSlider, 1, "0"),
        "GlyphStretch" => new NumericInputSpec(GlyphStretchSlider, 1, "0"),
        "GlyphWeight" => new NumericInputSpec(GlyphWeightSlider, 100, "0.#"),
        "SignalHue" => new NumericInputSpec(SignalHueSlider, 1, "0"),
        "SignalBrightness" => new NumericInputSpec(
            SignalBrightnessSlider,
            100,
            "0.#"),
        "BackgroundHue" => new NumericInputSpec(BackgroundHueSlider, 1, "0"),
        "BackgroundBrightness" => new NumericInputSpec(
            BackgroundBrightnessSlider,
            100,
            "0.#"),
        "Duration" => new NumericInputSpec(
            DurationSlider,
            1,
            "0.#",
            AppSettings.MinimumImageDurationSeconds,
            AppSettings.MaximumImageDurationSeconds),
        "ImageExpressiveness" => new NumericInputSpec(ImageExpressivenessSlider, 100, "0.#"),
        "ImageGlyphMatch" => new NumericInputSpec(ImageGlyphMatchSlider, 100, "0.#"),
        "ImageStability" => new NumericInputSpec(ImageStabilitySlider, 100, "0.#"),
        "ImageResistance" => new NumericInputSpec(ImageResistanceSlider, 100, "0.#"),
        "ImageBrightness" => new NumericInputSpec(ImageBrightnessSlider, 100, "0.#"),
        "ImageLocalContrast" => new NumericInputSpec(ImageLocalContrastSlider, 100, "0.#"),
        "ImageDetailStrength" => new NumericInputSpec(ImageDetailStrengthSlider, 100, "0.#"),
        "ImageEdgeStrength" => new NumericInputSpec(ImageEdgeStrengthSlider, 100, "0.#"),
        "ImageShadowBalance" => new NumericInputSpec(ImageShadowBalanceSlider, 100, "0.#"),
        "ImagePaletteAdaptation" => new NumericInputSpec(ImagePaletteAdaptationSlider, 100, "0.#"),
        "ImageToneCalmness" => new NumericInputSpec(ImageToneCalmnessSlider, 100, "0.#"),
        "AttackIdleMinutes" => new NumericInputSpec(
            AttackIdleMinutesSlider,
            1,
            "0.#",
            1.0,
            1440.0),
        "AttackTransitionSeconds" => new NumericInputSpec(
            AttackTransitionSecondsSlider,
            1,
            "0.#",
            AppSettings.MinimumAttackTransitionSeconds,
            AppSettings.MaximumAttackTransitionSeconds),
        _ => null
    };

    private double NumericValue(
        TextBox input,
        NumericInputSpec spec) =>
        input.Tag?.ToString() switch
        {
            "SpeedMin" => _speedMinValue,
            "SpeedMax" => _speedMaxValue,
            "MemoryMin" => _memoryMinValue,
            "MemoryMax" => _memoryMaxValue,
            "Duration" => _imageDurationSecondsValue,
            "AttackIdleMinutes" =>
                _attackIdleMinutesValue,
            "AttackTransitionSeconds" =>
                _attackTransitionSecondsValue,
            _ => spec.Slider.Value
        };

    private void SetNumericValue(
        TextBox input,
        NumericInputSpec spec,
        double value,
        bool useManualLimits)
    {
        double minimum = useManualLimits
            ? spec.ManualMinimum ?? spec.Slider.Minimum
            : spec.Slider.Minimum;
        double maximum = useManualLimits
            ? spec.ManualMaximum ?? spec.Slider.Maximum
            : spec.Slider.Maximum;
        value = NormalizeNumericValue(
            input,
            Math.Clamp(value, minimum, maximum));

        bool previousLoading = _loading;
        _loading = true;
        switch (input.Tag?.ToString())
        {
            case "SpeedMin":
                _speedMinValue = value;
                if (_speedMinValue > _speedMaxValue)
                {
                    _speedMaxValue = _speedMinValue;
                    SpeedMaxSlider.Value = Math.Clamp(
                        _speedMaxValue,
                        SpeedMaxSlider.Minimum,
                        SpeedMaxSlider.Maximum);
                    UpdateNumericInput(
                        CalibrationSpeedMaxInput,
                        _speedMaxValue * 100,
                        "0.#",
                        force: false);
                }
                break;
            case "SpeedMax":
                _speedMaxValue = value;
                if (_speedMaxValue < _speedMinValue)
                {
                    _speedMinValue = _speedMaxValue;
                    SpeedMinSlider.Value = Math.Clamp(
                        _speedMinValue,
                        SpeedMinSlider.Minimum,
                        SpeedMinSlider.Maximum);
                    UpdateNumericInput(
                        CalibrationSpeedMinInput,
                        _speedMinValue * 100,
                        "0.#",
                        force: false);
                }
                break;
            case "MemoryMin":
                _memoryMinValue = value;
                if (_memoryMinValue > _memoryMaxValue)
                {
                    _memoryMaxValue = _memoryMinValue;
                    MemoryMaxSlider.Value = Math.Clamp(
                        _memoryMaxValue,
                        MemoryMaxSlider.Minimum,
                        MemoryMaxSlider.Maximum);
                    UpdateNumericInput(
                        CalibrationMemoryMaxInput,
                        _memoryMaxValue * 100,
                        "0.#",
                        force: false);
                }
                break;
            case "MemoryMax":
                _memoryMaxValue = value;
                if (_memoryMaxValue < _memoryMinValue)
                {
                    _memoryMinValue = _memoryMaxValue;
                    MemoryMinSlider.Value = Math.Clamp(
                        _memoryMinValue,
                        MemoryMinSlider.Minimum,
                        MemoryMinSlider.Maximum);
                    UpdateNumericInput(
                        CalibrationMemoryMinInput,
                        _memoryMinValue * 100,
                        "0.#",
                        force: false);
                }
                break;
            case "Duration":
                _imageDurationSecondsValue = value;
                break;
            case "AttackIdleMinutes":
                _attackIdleMinutesValue = value;
                break;
            case "AttackTransitionSeconds":
                _attackTransitionSecondsValue = value;
                break;
        }
        spec.Slider.Value = Math.Clamp(
            value,
            spec.Slider.Minimum,
            spec.Slider.Maximum);
        _loading = previousLoading;
    }

    private static double NormalizeNumericValue(TextBox input, double value) =>
        input.Tag?.ToString() is "SignalMin" or "SignalMax"
            ? SignalModel.QuantizeStrength(value)
            : value;

    private static bool TryParseNumber(string text, out double value)
    {
        string normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static int FindTargetDigit(string text, int caret)
    {
        for (int index = Math.Clamp(caret, 0, text.Length); index < text.Length; index++)
        {
            if (char.IsDigit(text[index]))
                return index;
        }
        for (int index = Math.Min(caret - 1, text.Length - 1); index >= 0; index--)
        {
            if (char.IsDigit(text[index]))
                return index;
        }
        return -1;
    }

    private static int DigitExponent(string text, int digitIndex)
    {
        int separator = text.IndexOfAny([',', '.']);
        int integerEnd = separator >= 0 ? separator : text.Length;
        if (digitIndex < integerEnd)
        {
            int exponent = 0;
            for (int index = digitIndex + 1; index < integerEnd; index++)
            {
                if (char.IsDigit(text[index]))
                    exponent++;
            }
            return exponent;
        }

        int fractionalPlace = 0;
        for (int index = separator + 1; index <= digitIndex; index++)
        {
            if (char.IsDigit(text[index]))
                fractionalPlace++;
        }
        return -Math.Max(1, fractionalPlace);
    }

    private static int CountFractionDigits(string text)
    {
        int separator = text.IndexOfAny([',', '.']);
        if (separator < 0)
            return 0;
        int count = 0;
        for (int index = separator + 1; index < text.Length; index++)
        {
            if (char.IsDigit(text[index]))
                count++;
        }
        return count;
    }

    private static int FindCaretForExponent(string text, int exponent)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsDigit(text[index]) && DigitExponent(text, index) == exponent)
                return index;
        }
        return text.Length;
    }

    private static string FormatForEditing(double value, int decimalPlaces)
    {
        string format = decimalPlaces > 0
            ? "0." + new string('0', decimalPlaces)
            : "0";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private void RefreshLabels(bool force = false)
    {
        if (CalibrationSpeedMinInput is null)
            return;
        UpdateNumericInput(CalibrationSpeedMinInput, _speedMinValue * 100, "0.#", force);
        UpdateNumericInput(CalibrationSpeedMaxInput, _speedMaxValue * 100, "0.#", force);
        UpdateNumericInput(FontSizeInput, FontSizeSlider.Value, "0", force);
        UpdateNumericInput(GlyphStretchInput, GlyphStretchSlider.Value, "0", force);
        UpdateNumericInput(GlyphWeightInput, GlyphWeightSlider.Value * 100, "0.#", force);
        UpdateNumericInput(SignalHueInput, SignalHueSlider.Value, "0", force);
        UpdateNumericInput(
            SignalBrightnessInput,
            SignalBrightnessSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(BackgroundHueInput, BackgroundHueSlider.Value, "0", force);
        UpdateNumericInput(
            BackgroundBrightnessInput,
            BackgroundBrightnessSlider.Value * 100,
            "0.#",
            force);
        RefreshColorSwatches();
        RefreshTerminalPreview();
        UpdateNumericInput(DurationInput, _imageDurationSecondsValue, "0.#", force);
        UpdateNumericInput(ImageExpressivenessInput, ImageExpressivenessSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageGlyphMatchInput, ImageGlyphMatchSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageStabilityInput, ImageStabilitySlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageResistanceInput, ImageResistanceSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageBrightnessInput, ImageBrightnessSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageLocalContrastInput, ImageLocalContrastSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageDetailStrengthInput, ImageDetailStrengthSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageEdgeStrengthInput, ImageEdgeStrengthSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ImageShadowBalanceInput, ImageShadowBalanceSlider.Value * 100, "0.#", force);
        UpdateNumericInput(
            ImagePaletteAdaptationInput,
            ImagePaletteAdaptationSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            ImageToneCalmnessInput,
            ImageToneCalmnessSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            AttackIdleMinutesInput,
            _attackIdleMinutesValue,
            "0.#",
            force);
        UpdateNumericInput(
            AttackTransitionSecondsInput,
            _attackTransitionSecondsValue,
            "0.#",
            force);
        UpdateNumericInput(CalibrationDensityInput, DensitySlider.Value * 100, "0.#", force);
        UpdateNumericInput(CalibrationTrailMinInput, TrailMinSlider.Value * 100, "0.#", force);
        UpdateNumericInput(CalibrationTrailMaxInput, TrailMaxSlider.Value * 100, "0.#", force);
        UpdateNumericInput(
            CalibrationMemoryMinInput,
            _memoryMinValue * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationMemoryMaxInput,
            _memoryMaxValue * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationSignalMinInput,
            SignalMinSlider.Value * SignalModel.MaximumLevel,
            "0",
            force);
        UpdateNumericInput(
            CalibrationSignalMaxInput,
            SignalMaxSlider.Value * SignalModel.MaximumLevel,
            "0",
            force);
        UpdateNumericInput(
            CalibrationSignalGlowKeysInput,
            SignalGlowKeysSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationSignalGlowPriorityInput,
            SignalGlowPrioritySlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationHeadBrightnessInput,
            HeadBrightnessSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationHeadGlowInput,
            HeadGlowSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationHeadImpulseDecayInput,
            HeadImpulseDecaySlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationHeadImpulseProbabilityInput,
            HeadImpulseProbabilitySlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationHeadWeightInput,
            HeadWeightSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationInterceptionInput,
            InterceptionSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationStreamLifetimeMinInput,
            StreamLifetimeMinSlider.Value * 100,
            "0.#",
            force);
        UpdateNumericInput(
            CalibrationStreamLifetimeMaxInput,
            StreamLifetimeMaxSlider.Value * 100,
            "0.#",
            force);
        CurveEditor?.SetPreviewParameters(
            TrailMinSlider.Value,
            TrailMaxSlider.Value,
            DensitySlider.Value,
            InterceptionSlider.Value,
            StreamLifetimeMinSlider.Value,
            StreamLifetimeMaxSlider.Value,
            _speedMinValue,
            _speedMaxValue,
            _memoryMinValue,
            _memoryMaxValue,
            SignalMinSlider.Value,
            SignalMaxSlider.Value,
            SignalGlowKeysSlider.Value,
            SignalGlowPrioritySlider.Value,
            HeadBrightnessSlider.Value,
            HeadWeightSlider.Value,
            HeadGlowSlider.Value,
            HeadImpulseDecaySlider.Value,
            HeadImpulseProbabilitySlider.Value,
            SignalHueSlider.Value,
            SignalBrightnessSlider.Value);
    }

    private void RefreshColorSwatches()
    {
        if (SignalHueSwatch is null || BackgroundHueSwatch is null)
            return;
        SignalRgb signal = SignalColorModel.ToRgb(
            SignalHueSlider.Value,
            SignalBrightnessSlider.Value);
        SignalRgb signalMaximum = SignalColorModel.ToRgb(
            SignalHueSlider.Value,
            1.0);
        SignalRgb background = SignalColorModel.ToBackgroundRgb(
            BackgroundHueSlider.Value,
            BackgroundBrightnessSlider.Value);
        SignalRgb backgroundMaximum = SignalColorModel.ToBackgroundRgb(
            BackgroundHueSlider.Value,
            1.0);
        SignalHueSwatch.Background = FrozenBrush(signal);
        SignalBrightnessGradientStop.Color = MediaColor(signalMaximum);
        BackgroundHueSwatch.Background = FrozenBrush(background);
        BackgroundBrightnessGradientStop.Color = MediaColor(backgroundMaximum);
    }

    private static SolidColorBrush FrozenBrush(SignalRgb color)
    {
        SolidColorBrush brush = new(MediaColor(color));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color MediaColor(SignalRgb color) =>
        System.Windows.Media.Color.FromRgb(
            (byte)Math.Clamp((int)Math.Round(color.Red * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.Green * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.Blue * 255), 0, 255));

    private void RefreshTerminalPreview()
    {
        if (TerminalCodePreview is null
            || FontCombo is null
            || FontSizeSlider is null)
        {
            return;
        }

        TerminalCodePreview.SetParameters(
            SelectedTag(FontCombo, "MS Gothic"),
            FontSizeSlider.Value,
            GlyphStretchSlider.Value,
            GlyphWeightSlider.Value,
            SignalHueSlider.Value,
            SignalBrightnessSlider.Value,
            BackgroundHueSlider.Value,
            BackgroundBrightnessSlider.Value);
    }

    private static void UpdateNumericInput(TextBox input, double value, string format, bool force)
    {
        if (force || !input.IsKeyboardFocusWithin)
            input.Text = value.ToString(format, CultureInfo.CurrentCulture);
    }

    private void RefreshCurveEditor()
    {
        if (CurveEditor is null || CurvePresetCombo is null || CurveKindCombo is null)
            return;
        string kind = SelectedTag(CurveKindCombo, FlowCurveProfiles.LengthKind);
        bool terminal = kind == FlowCurveProfiles.TerminalKind;
        TerminalSettingsPanel.Visibility = terminal
            ? Visibility.Visible
            : Visibility.Collapsed;
        CurveCanvasBorder.Visibility = terminal
            ? Visibility.Collapsed
            : Visibility.Visible;
        CurveEditingHint.Visibility = terminal
            ? Visibility.Collapsed
            : Visibility.Visible;
        TerminalEditingHint.Visibility = terminal
            ? Visibility.Visible
            : Visibility.Collapsed;
        CurveEditingHint.Text =
            "Крайние точки закреплены. ЛКМ создаёт или перемещает внутреннюю точку; ПКМ либо Delete удаляет её.";
        UpdateCalibratorParameterPanels(kind);

        bool wasLoading = _loading;
        _loading = true;
        CurveProfilePanel.Visibility = terminal
            ? Visibility.Hidden
            : Visibility.Visible;
        if (terminal)
        {
            CurvePresetCombo.Items.Clear();
            _loading = wasLoading;
            return;
        }

        IReadOnlyList<CurvePoint> curve = CurveFor(kind);
        CurveEditor.SetCurve(kind, EffectiveCurveFor(kind));
        CurveEditor.SetPreviewParameters(
            TrailMinSlider.Value,
            TrailMaxSlider.Value,
            DensitySlider.Value,
            InterceptionSlider.Value,
            StreamLifetimeMinSlider.Value,
            StreamLifetimeMaxSlider.Value,
            _speedMinValue,
            _speedMaxValue,
            _memoryMinValue,
            _memoryMaxValue,
            SignalMinSlider.Value,
            SignalMaxSlider.Value,
            SignalGlowKeysSlider.Value,
            SignalGlowPrioritySlider.Value,
            HeadBrightnessSlider.Value,
            HeadWeightSlider.Value,
            HeadGlowSlider.Value,
            HeadImpulseDecaySlider.Value,
            HeadImpulseProbabilitySlider.Value,
            SignalHueSlider.Value,
            SignalBrightnessSlider.Value);
        LoadCurveAdjustmentControls(kind);
        CurvePresetCombo.Items.Clear();
        bool impulse = kind == FlowCurveProfiles.HeadPulseKind;
        CurveProfilePanel.Visibility = impulse
            ? Visibility.Hidden
            : Visibility.Visible;
        CurveVerticalShiftColumn.Width = impulse
            ? new GridLength(0)
            : new GridLength(22);
        CurveHorizontalShiftRow.Height = impulse
            ? new GridLength(0)
            : new GridLength(22);
        if (impulse)
        {
            _loading = wasLoading;
            return;
        }
        string matchingPreset = "Custom";
        foreach ((string id, string name) in FlowCurveProfiles.Presets(kind))
        {
            CurvePresetCombo.Items.Add(new ComboBoxItem
            {
                Content = name,
                Tag = id
            });
            if (FlowCurveMath.Equivalent(
                curve,
                FlowCurveProfiles.Create(kind, id),
                increasing: FlowCurveProfiles.IsIncreasing(kind),
                tolerance: 0.002))
            {
                matchingPreset = id;
            }
        }
        CurvePresetCombo.Items.Add(new ComboBoxItem
        {
            Content = kind is FlowCurveProfiles.SpeedKind
                or FlowCurveProfiles.SignalKind
                or FlowCurveProfiles.FilterKind
                or FlowCurveProfiles.MemoryKind
                ? "Пользовательская"
                : "Пользовательский",
            Tag = "Custom"
        });
        SelectByTag(CurvePresetCombo, matchingPreset);
        _loading = wasLoading;
    }

    private void UpdateCalibratorParameterPanels(string kind)
    {
        if (LengthToolbarPanel is null
            || SignalToolbarPanel is null
            || SpeedToolbarPanel is null
            || FilterToolbarPanel is null
            || MemoryToolbarPanel is null
            || HeadPulseToolbarPanel is null
            || LengthAxisPanel is null
            || SignalAxisPanel is null
            || SpeedAxisPanel is null
            || FilterAxisPanel is null
            || MemoryAxisPanel is null
            || NormalizedAxisPanel is null)
        {
            return;
        }

        LengthToolbarPanel.Visibility =
            kind == FlowCurveProfiles.LengthKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        LengthAxisPanel.Visibility =
            kind == FlowCurveProfiles.LengthKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        SignalToolbarPanel.Visibility =
            kind == FlowCurveProfiles.SignalKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        SignalAxisPanel.Visibility =
            kind == FlowCurveProfiles.SignalKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        SpeedToolbarPanel.Visibility =
            kind == FlowCurveProfiles.SpeedKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        SpeedAxisPanel.Visibility =
            kind == FlowCurveProfiles.SpeedKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        FilterToolbarPanel.Visibility =
            kind == FlowCurveProfiles.FilterKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        FilterAxisPanel.Visibility =
            kind == FlowCurveProfiles.FilterKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        MemoryToolbarPanel.Visibility =
            kind == FlowCurveProfiles.MemoryKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        MemoryAxisPanel.Visibility =
            kind == FlowCurveProfiles.MemoryKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        HeadPulseToolbarPanel.Visibility =
            kind == FlowCurveProfiles.HeadPulseKind
                ? Visibility.Visible
                : Visibility.Collapsed;
        NormalizedAxisPanel.Visibility =
            kind == FlowCurveProfiles.HeadPulseKind
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private IReadOnlyList<CurvePoint> CurveFor(string kind) => kind switch
    {
        FlowCurveProfiles.SpeedKind => _speedCurve,
        FlowCurveProfiles.SignalKind => _signalCurve,
        FlowCurveProfiles.FilterKind => _filterCurve,
        FlowCurveProfiles.MemoryKind => _memoryCurve,
        FlowCurveProfiles.HeadPulseKind => FlowCurveProfiles.DefaultHeadPulse(),
        _ => _lengthCurve
    };

    private IReadOnlyList<CurvePoint> EffectiveCurveFor(string kind)
    {
        IReadOnlyList<CurvePoint> curve = CurveFor(kind);
        if (kind == FlowCurveProfiles.HeadPulseKind)
            return curve;
        return FlowCurveMath.ApplyAdjustment(
            curve,
            FlowCurveProfiles.IsIncreasing(kind),
            AdjustmentFor(kind),
            invertVerticalShift: kind is FlowCurveProfiles.SpeedKind
                or FlowCurveProfiles.FilterKind);
    }

    private CurveAdjustment AdjustmentFor(string kind)
    {
        if (!_curveAdjustments.TryGetValue(kind, out CurveAdjustment? adjustment))
        {
            adjustment = new CurveAdjustment();
            _curveAdjustments[kind] = adjustment;
        }
        return adjustment;
    }

    private CurveAdjustment AdjustmentForSelectedCurve() =>
        AdjustmentFor(SelectedTag(CurveKindCombo, FlowCurveProfiles.SpeedKind));

    private void SetAdjustment(string kind, CurveAdjustment adjustment) =>
        _curveAdjustments[kind] = adjustment.Copy();

    private void LoadCurveAdjustmentControls(string kind)
    {
        CurveAdjustment adjustment = kind == FlowCurveProfiles.HeadPulseKind
            ? new CurveAdjustment()
            : AdjustmentFor(kind);
        CurveCharacterSlider.Value = adjustment.Character;
        CurveHorizontalShiftSlider.Value = adjustment.HorizontalShift;
        CurveVerticalShiftSlider.Value = adjustment.VerticalShift;
    }

    private void SetCurve(string kind, IReadOnlyList<CurvePoint> curve)
    {
        if (kind == FlowCurveProfiles.SpeedKind)
        {
            _speedCurve = FlowCurveMath.Normalize(curve, increasing: true);
            return;
        }
        if (kind == FlowCurveProfiles.MemoryKind)
        {
            _memoryCurve = FlowCurveMath.Normalize(curve, increasing: true);
            return;
        }
        if (kind == FlowCurveProfiles.SignalKind)
        {
            _signalCurve = FlowCurveMath.Normalize(curve, increasing: true);
            return;
        }
        if (kind == FlowCurveProfiles.FilterKind)
        {
            _filterCurve = FlowCurveMath.Normalize(curve, increasing: true);
            return;
        }
        if (kind == FlowCurveProfiles.HeadPulseKind)
            return;
        _lengthCurve = FlowCurveMath.Normalize(curve, increasing: true);
    }

    private void UpdateImagePreparationUi()
    {
        if (ImagePreparationCustomPanel is null
            || ImagePreparationDescription is null
            || ImageStructurePanel is null
            || ImagePreparationModeCombo is null)
        {
            return;
        }
        string mode = SelectedTag(ImagePreparationModeCombo, "Auto");
        ImagePreparationCustomPanel.Visibility = mode == "Custom"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ImageStructurePanel.IsEnabled = mode == "Custom";
        ImageStructurePanel.Opacity = mode == "Custom" ? 1.0 : 0.58;
        ImagePreparationDescription.Text = mode switch
        {
            "None" => "Исходная светимость без локального усиления и поиска контуров.",
            "Portrait" => "Мягко поднимает тени и сохраняет крупные черты без лишнего шума.",
            "Contours" => "Ослабляет заливки и подчёркивает границы форм и мелкие детали.",
            "Silhouette" => "Разделяет кадр на уверенные светлые и тёмные массы с тонким контуром.",
            "Custom" => "Полный ручной анализ: тон, детали, палитра и способ выделения структуры.",
            _ => "Оценивает диапазон, шум и количество деталей отдельно для каждого изображения."
        };
    }

    private void UpdateCollapsibleSections()
    {
        if (DatabaseContentPanel is not null)
        {
            MonitorProfile? profile = _monitors.Count == 0
                ? null
                : MonitorTopology.Find(
                    _draftSettings.MonitorProfiles,
                    _selectedMonitorId);
            bool ownsDatabase = profile is null
                || profile.DatabaseMode == MonitorLinkMode.Isolated;
            DatabaseContentPanel.Visibility =
                ownsDatabase && ImageModeCheck.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (AttackContentPanel is not null)
        {
            AttackContentPanel.Visibility =
                AttackSystemEnabledCheck.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private static void SelectByTag(Selector selector, string tag)
    {
        foreach (object item in selector.Items)
        {
            if (item is FrameworkElement element
                && string.Equals(
                    element.Tag?.ToString(),
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                selector.SelectedItem = item;
                return;
            }
        }
        selector.SelectedIndex = 0;
    }

    private void PopulateFontFamilies()
    {
        IEnumerable<string> familyNames;
        try
        {
            familyNames = System.Windows.Media.Fonts.SystemFontFamilies
                .Select(family => family.Source.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => string.Equals(name, "MS Gothic", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            familyNames = ["MS Gothic", "Yu Gothic UI", "Consolas"];
        }

        FontCombo.Items.Clear();
        foreach (string familyName in familyNames)
        {
            FontCombo.Items.Add(new ComboBoxItem
            {
                Content = familyName,
                Tag = familyName
            });
        }
        EnsureFontOption("MS Gothic");
    }

    private void EnsureFontOption(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return;
        foreach (object item in FontCombo.Items)
        {
            if (item is ComboBoxItem comboItem
                && string.Equals(
                    comboItem.Tag?.ToString(),
                    familyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        FontCombo.Items.Insert(0, new ComboBoxItem
        {
            Content = familyName,
            Tag = familyName
        });
    }

    private static string SelectedTag(Selector selector, string fallback) =>
        (selector.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? fallback;

    private static bool IsDescendantOf(
        DependencyObject element,
        DependencyObject ancestor) =>
        FindAncestor<DependencyObject>(element, ancestor) is not null;

    private static T? FindAncestor<T>(
        DependencyObject? element,
        DependencyObject? exactMatch = null)
        where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is T candidate
                && (exactMatch is null || ReferenceEquals(current, exactMatch)))
            {
                return candidate;
            }

            current = current is Visual
                or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private enum PlaylistEntryStateOperation
    {
        EnableSelected,
        DisableSelected
    }

    private readonly record struct NumericInputSpec(
        Slider Slider,
        double DisplayScale,
        string Format,
        double? ManualMinimum = null,
        double? ManualMaximum = null);
}
