using System.Drawing;
using System.IO;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _imageModeItem;
    private readonly Icon _icon;

    public TrayService(
        Action showSettings,
        Action togglePaused,
        Action toggleImageMode,
        Action nextImage,
        Action refreshDesktop,
        Action exit)
    {
        _icon = CreateIcon();
        System.Windows.Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Открыть операторскую консоль", null, (_, _) => RunOnUi(showSettings));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Остановить и скрыть поток", null, (_, _) => RunOnUi(togglePaused));
        _imageModeItem = new System.Windows.Forms.ToolStripMenuItem("Проявлять изображения", null, (_, _) => RunOnUi(toggleImageMode));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_imageModeItem);
        menu.Items.Add("Следующее изображение", null, (_, _) => RunOnUi(nextImage));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Переподключить к рабочему столу", null, (_, _) => RunOnUi(refreshDesktop));
        menu.Items.Add("Выход", null, (_, _) => RunOnUi(exit));

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = AppVersion.DisplayName,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RunOnUi(showSettings);
    }

    public void Update(
        AppSettings settings,
        bool isManuallyPaused,
        bool isPausedByFullscreenApp)
    {
        _pauseItem.Checked = isManuallyPaused || isPausedByFullscreenApp;
        _pauseItem.Enabled = !isPausedByFullscreenApp;
        _pauseItem.Text = isPausedByFullscreenApp
            ? "Полноэкранное приложение — поток плавно остановлен"
            : isManuallyPaused
                ? "Возобновить поток"
                : "Остановить и скрыть поток";
        _imageModeItem.Checked = settings.ImageMode;
    }

    public void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "Wallpaper Matrix — ошибка вывода";
        _notifyIcon.BalloonTipText = message.Length <= 240
            ? message
            : message[..237] + "…";
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(9000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        try
        {
            Uri resourceUri = new(
                "pack://application:,,,/Assets/WallpaperMatrix.ico",
                UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo? resource =
                System.Windows.Application.GetResourceStream(resourceUri);
            if (resource is not null)
            {
                using Stream stream = resource.Stream;
                using Icon embedded = new(stream, 32, 32);
                return (Icon)embedded.Clone();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Не удалось загрузить встроенный значок области уведомлений.",
                exception);
        }

        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            Icon? associated = Icon.ExtractAssociatedIcon(executable);
            if (associated is not null)
                return associated;
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static void RunOnUi(Action action)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
    }
}
