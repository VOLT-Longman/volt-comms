using System.Windows;
using VoltComms.Core;
using Forms = System.Windows.Forms;

namespace VoltComms;

/// <summary>앱 진입점: 설정/로거 초기화와 트레이 아이콘을 담당한다.</summary>
public partial class App : System.Windows.Application
{
    private Logger? _log;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _main;

    // 중복 실행 방지(단일 인스턴스). Local\ 접두사로 현재 로그인 세션 단위로 막는다.
    private const string InstanceMutexName = @"Local\VoltComms.SingleInstance.A3F1";
    private const string ShowEventName = @"Local\VoltComms.ShowExisting.A3F1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // 이미 다른 인스턴스가 실행 중이면, 그쪽 창을 띄우라고 신호한 뒤 조용히 종료한다.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                    using (existing) existing.Set();
            }
            catch
            {
                // 신호 실패해도 중복 실행만 막으면 되므로 그대로 종료한다.
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // 첫 인스턴스: 이후 다른 인스턴스가 보내는 '창 띄우기' 신호를 대기한다.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_main != null) RestoreWindow(_main);
            })),
            null, Timeout.Infinite, executeOnlyOnce: false);

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
                $"설정 파일(config.json)을 읽을 수 없습니다:\n{ex.Message}\n\n파일을 지운 뒤 다시 실행하면 초기화됩니다.",
                "VOLT 무전기", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _log.Info($"=== VOLT 무전기 시작 (설정: {AppConfig.ConfigPath}) ===");

        _main = new MainWindow(cfg, _log);
        SetupTray(_main);

        bool firstRun = created || !cfg.LooksConfigured;
        if (firstRun)
        {
            // 첫 실행: 창을 띄우고 곧바로 설정 화면 안내.
            _main.Show();
            _main.OpenSettings();
        }
        else if (cfg.StartMinimized)
        {
            ShowBalloonOnce();
        }
        else
        {
            _main.Show();
        }
    }

    private void ShowBalloonOnce()
    {
        if (_tray == null) return;
        _tray.BalloonTipTitle = "VOLT 무전기";
        _tray.BalloonTipText = "트레이에서 실행 중입니다. 아이콘을 더블클릭하면 창이 열립니다.";
        _tray.ShowBalloonTip(3000);
    }

    private void SetupTray(MainWindow win)
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "VOLT 무전기",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => RestoreWindow(win));
        menu.Items.Add("설정", null, (_, _) => { RestoreWindow(win); win.OpenSettings(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow(win);
    }

    /// <summary>송신/수신/대기 상태에 따라 트레이 아이콘과 툴팁을 바꾼다.</summary>
    public static void SetTray(System.Drawing.Icon icon, string tooltip)
    {
        if (Current is not App { _tray: { } tray }) return;
        tray.Icon = icon;
        // NotifyIcon.Text 는 63자 제한
        tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
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
        // 단일 인스턴스 자원 정리.
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _instanceMutex?.Dispose();
        _log?.Info("=== VOLT 무전기 종료 ===");
    }
}
