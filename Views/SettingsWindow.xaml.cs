using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperMatrix.Models;
using WallpaperMatrix.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace WallpaperMatrix.Views;

public partial class SettingsWindow : Window
{
    private sealed class PresetChoice
    {
        public string Label { get; init; } = "ОПЕРАТОР";
        public OperatorPreset? Preset { get; init; }
        public string Details => Preset is null
            ? "Параметры не связаны с глобальным пресетом"
            : $"Изменён: {Preset.ModifiedLabel}";
    }

    private sealed class MonitorChoice
    {
        public required MonitorDescriptor Monitor { get; init; }
        public string Label => Monitor.Label;
    }

    private sealed class MonitorRouteChoice
    {
        public required MonitorLinkMode Mode { get; init; }
        public string SourceMonitorId { get; init; } = "";
        public required string Label { get; init; }
    }

    private AppSettings _source = new();
    private AppSettings _draftSettings = new();
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private string _selectedMonitorId = "";
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _fontPreviewTimer;
    private readonly DispatcherTimer _colorPreviewTimer;
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
    private long _lastWheelTick;
    private bool _mainScrollGesture;
    private string _runtimeStatus = "СОСТОЯНИЕ ВЫВОДА НЕ ПОЛУЧЕНО";
    private string _diagnosticLogPath = DiagnosticLog.LogPath;
    private readonly PresetStore _presetStore = new();
    private List<OperatorPreset> _presets = [];
    private string _selectedPresetId = "";
    private bool _updatingPresetUi;
    private int _externalImageLaunchInProgress;

    public event Action<AppSettings>? SettingsApplied;
    public event Action<AppSettings>? PlaylistsSaved;
    public event Action<AppSettings>? SettingsPreviewed;
    public event Action<AppSettings, string, string>? ImageRequested;
    public event Action<bool>? PauseRequested;
    public event Action? AttackRequested;

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
    }

    public void LoadSettings(AppSettings settings)
    {
        LoadSettingsCore(settings, preserveAppliedSettings: false);
    }

    private void LoadSettingsCore(
        AppSettings settings,
        bool preserveAppliedSettings)
    {
        AppSettings container = settings.Copy();
        _monitors = MonitorCatalog.Capture();
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
        ClockEnabledCheck.IsChecked = displaySettings.ClockEnabled;
        ClockHorizontalMarginSlider.Value = displaySettings.ClockHorizontalMarginCells;
        ClockVerticalMarginSlider.Value = displaySettings.ClockVerticalMarginCells;
        ClockBrightnessSlider.Value = displaySettings.ClockBrightness;
        ClockWeightSlider.Value = displaySettings.ClockWeight;
        _playlists = displaySettings.ImagePlaylists
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
        SelectByTag(ImageFitCombo, displaySettings.ImageFit);
        SelectByTag(ImagePreparationModeCombo, displaySettings.ImagePreparationMode);
        SelectByTag(ImageStructureModeCombo, displaySettings.ImageStructureMode);
        SelectByTag(ClockPositionCombo, displaySettings.ClockPosition);
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

    public void SetRuntimeStatus(string status, bool isError, string diagnosticLogPath)
    {
        _runtimeStatus = status;
        _diagnosticLogPath = diagnosticLogPath;
        if (RuntimeStatusBorder is null
            || RuntimeStatusTitle is null
            || RuntimeStatusText is null)
            return;
        RuntimeStatusText.Text = status;
        RuntimeStatusBorder.Visibility = Visibility.Visible;
        RuntimeStatusBorder.Background = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(36, 16, 6)
                : System.Windows.Media.Color.FromRgb(7, 27, 16));
        RuntimeStatusBorder.BorderBrush = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(216, 120, 42)
                : System.Windows.Media.Color.FromRgb(35, 138, 75));
        RuntimeStatusTitle.Foreground = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(255, 177, 102)
                : System.Windows.Media.Color.FromRgb(131, 255, 170));
        RuntimeStatusText.Foreground = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(255, 213, 174)
                : System.Windows.Media.Color.FromRgb(189, 236, 202));
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
        display.ClockEnabled = ClockEnabledCheck.IsChecked == true;
        display.ClockPosition = SelectedTag(ClockPositionCombo, "TopRight");
        display.ClockHorizontalMarginCells = (int)Math.Round(ClockHorizontalMarginSlider.Value);
        display.ClockVerticalMarginCells = (int)Math.Round(ClockVerticalMarginSlider.Value);
        display.ClockBrightness = ClockBrightnessSlider.Value;
        display.ClockWeight = ClockWeightSlider.Value;
        display.ImagePlaylists = _playlists
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
        updated.ActivePresetId = _selectedPresetId;
        display.FontFamily = SelectedTag(FontCombo, "MS Gothic");
        display.ImageFit = SelectedTag(ImageFitCombo, "Uniform");
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
        if (MonitorDeviceCombo is null
            || MonitorFlowModeCombo is null
            || MonitorDatabaseModeCombo is null)
        {
            return;
        }

        MonitorProfile selected = SelectedMonitorProfile(_draftSettings);
        List<MonitorChoice> devices = _monitors
            .Select(monitor => new MonitorChoice { Monitor = monitor })
            .ToList();
        MonitorDeviceCombo.ItemsSource = devices;
        MonitorDeviceCombo.SelectedItem = devices.FirstOrDefault(choice =>
            string.Equals(
                choice.Monitor.Id,
                selected.MonitorId,
                StringComparison.OrdinalIgnoreCase));

        MonitorDescriptor? descriptor = _monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.Id,
                selected.MonitorId,
                StringComparison.OrdinalIgnoreCase));
        MonitorDeviceDetails.Text = descriptor is null
            ? selected.LastKnownName
            : $"{descriptor.SystemName} // "
                + $"{descriptor.Bounds.Width}×{descriptor.Bounds.Height} // "
                + $"X {descriptor.Bounds.Left}, Y {descriptor.Bounds.Top}";

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
        UpdateMonitorRouteNotices();
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
                         StringComparison.OrdinalIgnoreCase)))
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
                         StringComparison.OrdinalIgnoreCase)))
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

    private void MonitorDeviceCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading
            || MonitorDeviceCombo.SelectedItem is not MonitorChoice choice
            || string.Equals(
                choice.Monitor.Id,
                _selectedMonitorId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _draftSettings = ReadSettingsFromControls();
        _selectedMonitorId = choice.Monitor.Id;
        LoadSettingsCore(_draftSettings, preserveAppliedSettings: true);
        QueuePreview();
    }

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
        MonitorRouteChoice? choice)
    {
        if (_loading || choice is null)
            return;

        AppSettings current = ReadSettingsFromControls();
        MonitorTopology.SetRoute(
            current.MonitorProfiles,
            _monitors,
            domain,
            _selectedMonitorId,
            choice.Mode,
            choice.SourceMonitorId);
        LoadSettingsCore(current, preserveAppliedSettings: true);
        QueuePreview();
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
        MonitorChoice? choice = (MonitorDeviceCombo.ItemsSource
                as IEnumerable<MonitorChoice>)
            ?.FirstOrDefault(item => string.Equals(
                item.Monitor.Id,
                source,
                StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return;

        _draftSettings = ReadSettingsFromControls();
        _selectedMonitorId = choice.Monitor.Id;
        LoadSettingsCore(_draftSettings, preserveAppliedSettings: true);
        QueuePreview();
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
            PresetCombo.Items.Add(operatorChoice);
            foreach (OperatorPreset preset in _presets)
            {
                PresetCombo.Items.Add(new PresetChoice
                {
                    Label = preset.Name,
                    Preset = preset
                });
            }

            PresetChoice selected = PresetCombo.Items
                .OfType<PresetChoice>()
                .FirstOrDefault(choice => string.Equals(
                    choice.Preset?.Id,
                    requestedPresetId,
                    StringComparison.OrdinalIgnoreCase))
                ?? operatorChoice;
            PresetCombo.SelectedItem = selected;
            PresetCombo.IsEnabled = _presets.Count > 0;
            _selectedPresetId = selected.Preset?.Id ?? "";
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

        string requestedId = choice.Preset?.Id ?? "";
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
        if (choice.Preset is not null)
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
        IReadOnlyList<MonitorDescriptor> monitors = _monitors.Count > 0
            ? _monitors
            : MonitorCatalog.Capture();
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

    private OperatorPreset? CurrentPreset() =>
        _presets.FirstOrDefault(preset => string.Equals(
            preset.Id,
            _selectedPresetId,
            StringComparison.OrdinalIgnoreCase));

    private void UpdatePresetActionButtons()
    {
        if (SavePresetButton is null
            || ResetPresetButton is null
            || DeletePresetButton is null)
        {
            return;
        }

        OperatorPreset? preset = CurrentPreset();
        DeletePresetButton.IsEnabled = preset is not null;
        bool hasPresetChanges = preset is not null
            && !HasInvalidNumericInput()
            && !PresetEquivalentForCurrentTopology(
                ReadSettingsFromControls(),
                preset);
        SavePresetButton.Visibility = hasPresetChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResetPresetButton.Visibility = hasPresetChanges
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool PresetEquivalentForCurrentTopology(
        AppSettings settings,
        OperatorPreset preset)
    {
        IReadOnlyList<MonitorDescriptor> monitors = _monitors.Count > 0
            ? _monitors
            : MonitorCatalog.Capture();
        AppSettings baseline = MonitorPresetAdapter.Adapt(
            preset.Settings,
            settings,
            monitors);
        OperatorPlaylistBinding.Apply(baseline, settings);
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
        AppSettings sourceDisplay = SelectedMonitorSettings(_source);
        sourceDisplay.ImagePlaylists = savedPlaylists
            .Select(item => item.Copy())
            .ToList();
        sourceDisplay.ActiveImagePlaylistId = _activePlaylistId;
        SynchronizeLegacySettings(_source);
        AppSettings liveDraft = ReadSettingsFromControls();
        _draftSettings = liveDraft.Copy();
        PlaylistsSaved?.Invoke(liveDraft);
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

    private void SettingsWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PlaylistList is null
            || MainScrollViewer is null
            || e.OriginalSource is not DependencyObject origin)
        {
            return;
        }

        bool overPlaylist = IsDescendantOf(origin, PlaylistList);
        long now = Environment.TickCount64;
        if (now - _lastWheelTick > 340)
            _mainScrollGesture = !overPlaylist;
        _lastWheelTick = now;

        // A wheel gesture keeps its original scroll target until the operator
        // pauses. Moving the pointer across the playlist can therefore no
        // longer seize a continuous page scroll halfway through.
        if (!overPlaylist || !_mainScrollGesture)
            return;

        double step = e.Delta / 120.0 * 52.0;
        MainScrollViewer.ScrollToVerticalOffset(Math.Clamp(
            MainScrollViewer.VerticalOffset - step,
            0,
            MainScrollViewer.ScrollableHeight));
        e.Handled = true;
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
            if (!File.Exists(entry.Path))
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
        RefreshPlaylistEntries();
        _loading = wasLoading;
    }

    private void RefreshPlaylistEntries()
    {
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
            case "ClockHorizontalMargin": ClockHorizontalMarginSlider.Value = standard.ClockHorizontalMarginCells; break;
            case "ClockVerticalMargin": ClockVerticalMarginSlider.Value = standard.ClockVerticalMarginCells; break;
            case "ClockBrightness": ClockBrightnessSlider.Value = standard.ClockBrightness; break;
            case "ClockWeight": ClockWeightSlider.Value = standard.ClockWeight; break;
            case "FontFamily":
                EnsureFontOption(standard.FontFamily);
                SelectByTag(FontCombo, standard.FontFamily);
                break;
            case "ImagePreparationMode": SelectByTag(ImagePreparationModeCombo, standard.ImagePreparationMode); break;
            case "ImageStructureMode": SelectByTag(ImageStructureModeCombo, standard.ImageStructureMode); break;
            case "ImageFit": SelectByTag(ImageFitCombo, standard.ImageFit); break;
            case "FramesPerSecond": SelectByTag(FpsCombo, standard.FramesPerSecond.ToString()); break;
            case "ClockEnabled": ClockEnabledCheck.IsChecked = standard.ClockEnabled; break;
            case "ClockPosition": SelectByTag(ClockPositionCombo, standard.ClockPosition); break;
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
            case "CurrentContour":
                ResetCurrentContour(standard);
                break;
            case "ClockBlock":
                ResetClockBlock(standard);
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

    private void ResetClockBlock(AppSettings standard)
    {
        SelectByTag(ClockPositionCombo, standard.ClockPosition);
        ClockHorizontalMarginSlider.Value = standard.ClockHorizontalMarginCells;
        ClockVerticalMarginSlider.Value = standard.ClockVerticalMarginCells;
        ClockBrightnessSlider.Value = standard.ClockBrightness;
        ClockWeightSlider.Value = standard.ClockWeight;
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
        SelectByTag(ImageFitCombo, standard.ImageFit);
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
        yield return ClockHorizontalMarginInput;
        yield return ClockVerticalMarginInput;
        yield return ClockBrightnessInput;
        yield return ClockWeightInput;
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
        CommitNumericInput(ClockHorizontalMarginInput);
        CommitNumericInput(ClockVerticalMarginInput);
        CommitNumericInput(ClockBrightnessInput);
        CommitNumericInput(ClockWeightInput);
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
        "ClockHorizontalMargin" => new NumericInputSpec(ClockHorizontalMarginSlider, 1, "0"),
        "ClockVerticalMargin" => new NumericInputSpec(ClockVerticalMarginSlider, 1, "0"),
        "ClockBrightness" => new NumericInputSpec(ClockBrightnessSlider, 100, "0.#"),
        "ClockWeight" => new NumericInputSpec(ClockWeightSlider, 100, "0.#"),
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
            1.0,
            30.0),
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
        UpdateNumericInput(ClockHorizontalMarginInput, ClockHorizontalMarginSlider.Value, "0", force);
        UpdateNumericInput(ClockVerticalMarginInput, ClockVerticalMarginSlider.Value, "0", force);
        UpdateNumericInput(ClockBrightnessInput, ClockBrightnessSlider.Value * 100, "0.#", force);
        UpdateNumericInput(ClockWeightInput, ClockWeightSlider.Value * 100, "0.#", force);
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
        CurveEditingHint.Visibility = Visibility.Visible;
        CurveEditingHint.Text = terminal
            ? "Статичный фрагмент показывает шрифт, геометрию, толщину, спектр и яркость символов и фактический фон."
            : "Крайние точки закреплены. ЛКМ создаёт или перемещает внутреннюю точку; ПКМ либо Delete удаляет её.";
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
        if (ClockContentPanel is not null)
        {
            ClockContentPanel.Visibility = ClockEnabledCheck.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
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
