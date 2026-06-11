using System.Windows;
using VoltComms.Core;
using Forms = System.Windows.Forms;

namespace VoltComms;

/// <summary>앱 진입점: 설정/로거 초기화와 트레이 아이콘을 담당한다.</summary>
public partial class App : System.Windows.Application
{
    private Logger? _log;
    private Forms.NotifyIcon? _tray;

    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        _log = new Logger();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Info($"[치명적 오류] {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Info($"[처리되지 않은 오류] {args.Exception}");
            args.Handled = true;
        };

        AppConfig cfg;
        bool created;
        try
        {
            cfg = AppConfig.LoadOrCreate(out created);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"config.json 을 읽을 수 없습니다:\n{ex.Message}\n\n파일을 고치거나 지운 뒤 다시 실행하세요.",
                "VOLT 무전기", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _log.Info($"=== VOLT 무전기 시작 (설정: {AppConfig.ConfigPath}) ===");

        var win = new MainWindow(cfg, _log, created);
        SetupTray(win);
        win.Show();
    }

    private void SetupTray(MainWindow win)
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "VOLT 무전기",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => RestoreWindow(win));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow(win);
    }

    private static void RestoreWindow(MainWindow win)
    {
        win.Show();
        win.WindowState = WindowState.Normal;
        win.Activate();
    }

    private void OnAppExit(object sender, ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _log?.Info("=== VOLT 무전기 종료 ===");
    }
}
