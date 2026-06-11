using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VoltComms.Core;
// UseWindowsForms 가 System.Drawing 을 암시적으로 가져와 충돌하므로 WPF 쪽으로 고정한다.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace VoltComms;

/// <summary>
/// 메인 창: 각 구성요소(제어/음성/오디오/PTT 훅)를 연결하고 상태를 표시한다.
/// PTT 상태 머신은 모두 UI 스레드에서만 갱신한다.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Brush ColIdle = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB4));
    private static readonly Brush ColTx = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
    private static readonly Brush ColDeny = new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x31));
    private static readonly Brush ColOk = new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x6E));
    private static readonly Brush ColBad = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));

    private readonly AppConfig _cfg;
    private readonly Logger _log;
    private readonly bool _configCreated;

    private ControlClient? _control;
    private VoiceClient? _voice;
    private AudioEngine? _audio;
    private PttHook? _hook;

    private bool _keyHeld;          // PTT 물리 키가 눌려 있는가
    private bool _granted;          // 서버가 발언권을 줬는가
    private DateTime _txStart;
    private int _maxTalkSec = 30;
    private int _statsTick;

    public MainWindow(AppConfig cfg, Logger log, bool configCreated)
    {
        _cfg = cfg;
        _log = log;
        _configCreated = configCreated;
        InitializeComponent();
        Topmost = cfg.AlwaysOnTop;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_configCreated || !_cfg.LooksConfigured)
        {
            System.Windows.MessageBox.Show(
                $"설정 파일을 먼저 채워 주세요:\n{AppConfig.ConfigPath}\n\n" +
                "server_host(서버 주소), token(인증 토큰), nickname(콜사인)을 입력하고 " +
                "앱을 다시 실행하면 됩니다.",
                "VOLT 무전기 — 초기 설정", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 오디오
        try
        {
            _audio = new AudioEngine(_log);
            _audio.LogDevices();
            _audio.Start(_cfg.InputDevice, _cfg.OutputDevice);
        }
        catch (Exception ex)
        {
            _log.Info($"[오디오] 초기화 실패: {ex}");
            System.Windows.MessageBox.Show(
                $"오디오 장치를 열 수 없습니다: {ex.Message}\n마이크/스피커 연결을 확인하세요.",
                "VOLT 무전기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 음성(UDP)
        _voice = new VoiceClient(_log);
        if (_audio != null)
        {
            _audio.EncodedFrame += (buf, len) => _voice.SendVoice(buf, len);
            _voice.VoiceFrameReceived += (sender2, opus) => _audio.PlayVoice(sender2, opus);
        }

        // 제어(WebSocket)
        _control = new ControlClient(_cfg, _log);
        _control.ConnectionChanged += ok => Dispatcher.BeginInvoke(() => OnConnectionChanged(ok));
        _control.MessageReceived += msg => Dispatcher.BeginInvoke(() => OnControlMessage(msg));
        _control.Start();

        // PTT 전역 훅
        try
        {
            _hook = new PttHook(_cfg.PttKey);
            _hook.PttDown += OnPttDown; // 훅은 UI 스레드 메시지 루프에서 콜백된다.
            _hook.PttUp += OnPttUp;
            _hook.Install();
            TxText.Text = $"대기 (PTT: {_hook.KeyLabel})";
        }
        catch (Exception ex)
        {
            _log.Info($"[PTT] 훅 설치 실패: {ex}");
            TxText.Text = "PTT 키 설정 오류 — config.json 확인";
            TxText.Foreground = ColBad;
        }

        // 계측/페일세이프 타이머
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    // ---------- PTT 상태 머신 (UI 스레드) ----------

    private void OnPttDown()
    {
        if (_control is not { IsConnected: true })
        {
            _audio?.DenyBeep();
            TxText.Text = "서버에 연결되어 있지 않음";
            TxText.Foreground = ColBad;
            return;
        }
        _keyHeld = true;
        _control.Send("ptt_start");
    }

    private void OnPttUp()
    {
        _keyHeld = false;
        if (_granted) StopTx(sendStop: true, "대기");
    }

    private void OnControlMessage(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "auth_ok":
                if (msg.MaxTalkSec > 0) _maxTalkSec = msg.MaxTalkSec;
                _voice?.Start(_cfg.ServerHost, msg.UdpPort, msg.SessionId);
                break;

            case "auth_fail":
                ConnText.Text = "● 인증 실패 — 토큰 확인";
                ConnText.Foreground = ColBad;
                break;

            case "udp_ok":
                _voice?.ConfirmUdp();
                break;

            case "ptt_granted":
                if (!_keyHeld)
                {
                    // 키를 이미 뗀 뒤 늦게 도착한 승인 — 곧바로 반납.
                    _control?.Send("ptt_stop");
                    break;
                }
                _granted = true;
                _txStart = DateTime.UtcNow;
                if (_audio != null) _audio.Transmitting = true;
                if (_cfg.GrantBeep) _audio?.GrantBeep();
                TxText.Text = "● 송신 중";
                TxText.Foreground = ColTx;
                break;

            case "ptt_denied":
                _audio?.DenyBeep();
                TxText.Text = $"거부 — {msg.Holder} 발언 중";
                TxText.Foreground = ColDeny;
                _log.Info($"[PTT] 거부됨 (보유자: {msg.Holder})");
                break;

            case "ptt_force_release":
                if (_granted)
                {
                    StopTx(sendStop: false, "발언권 강제 회수됨");
                    _audio?.DenyBeep();
                    _log.Info($"[PTT] 서버가 발언권 강제 회수: {msg.Reason}");
                }
                break;

            case "speaker":
                SpeakerText.Text = string.IsNullOrEmpty(msg.Nick) ? "—" : msg.Nick;
                SpeakerText.Foreground = string.IsNullOrEmpty(msg.Nick)
                    ? new SolidColorBrush(Color.FromRgb(0xD7, 0xE1, 0xEA))
                    : (Brush)new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x6E));
                break;

            case "roster":
                RosterText.Text = msg.Users is { Length: > 0 }
                    ? $"접속 {msg.Users.Length}명: {string.Join(", ", msg.Users)}"
                    : "";
                break;
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            ConnText.Text = $"● {_cfg.ServerHost} 연결됨";
            ConnText.Foreground = ColOk;
        }
        else
        {
            ConnText.Text = "● 연결 끊김 — 재접속 중…";
            ConnText.Foreground = ColBad;
            if (_granted) StopTx(sendStop: false, "대기");
            SpeakerText.Text = "—";
            RosterText.Text = "";
        }
    }

    private void StopTx(bool sendStop, string statusText)
    {
        _granted = false;
        if (_audio != null) _audio.Transmitting = false;
        if (sendStop) _control?.Send("ptt_stop");
        TxText.Text = $"{statusText} (PTT: {_hook?.KeyLabel ?? _cfg.PttKey})";
        TxText.Foreground = ColIdle;
    }

    // ---------- 주기 작업: 클라이언트 측 stuck-PTT 페일세이프 + 계측 표시 ----------

    private void OnTick()
    {
        if (_granted && (DateTime.UtcNow - _txStart).TotalSeconds > _maxTalkSec)
        {
            StopTx(sendStop: true, "최대 발언 시간 초과");
            _audio?.DenyBeep();
            _log.Info("[PTT] 클라이언트 페일세이프: 최대 발언 시간 초과로 자동 해제");
        }

        if (++_statsTick % 2 != 0) return; // 1초마다 표시 갱신
        var rtt = _control?.LastRttMs ?? -1;
        var (rx, lost) = _voice?.RxStats ?? (0, 0);
        double lossPct = rx + lost > 0 ? lost * 100.0 / (rx + lost) : 0;
        StatsText.Text = rtt >= 0
            ? $"RTT {rtt:F0} ms · 수신 손실 {lossPct:F1}% · 수신 {rx} · 송신 {_voice?.SentCount ?? 0}"
            : "RTT — · 손실 —";

        if (_statsTick % 20 == 0 && _control is { IsConnected: true }) // 10초마다 로그
            _log.Info($"[계측] RTT {rtt:F1}ms, 수신 {rx}패킷, 손실 {lost} ({lossPct:F1}%), 송신 {_voice?.SentCount ?? 0}패킷");
    }

    // ---------- 창 동작: 닫기 → 트레이로 ----------

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hook?.Dispose();
        _control?.Dispose();
        _voice?.Dispose();
        _audio?.Dispose();
        base.OnClosed(e);
    }
}
