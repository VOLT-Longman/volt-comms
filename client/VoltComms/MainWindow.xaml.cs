using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VoltComms.Core;
// UseWindowsForms 가 System.Drawing 을 암시적으로 가져와 충돌하므로 WPF 쪽으로 고정한다.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace VoltComms;

/// <summary>접속자 목록 한 줄. 발언 중이면 초록 점이 켜지고, 유저별 수신 음량을 가진다.</summary>
public sealed class RosterEntry : INotifyPropertyChanged
{
    private bool _isSpeaking;
    private int _volume = 100;
    public string Nick { get; init; } = "";
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
        }
    }

    /// <summary>이 접속자의 수신 음량 (0~200%). 슬라이더와 양방향 바인딩된다.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            if (_volume == value) return;
            _volume = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 메인 창: 각 구성요소(제어/음성/오디오/PTT 훅)를 연결하고 상태를 표시한다.
/// 설정 저장 시 RestartServices() 로 재시작해 즉시 적용한다.
/// PTT 상태 머신은 모두 UI 스레드에서만 갱신한다.
/// </summary>
public partial class MainWindow : Window
{
    // VOLT 웹사이트 디자인 토큰과 동기화 (App.xaml 팔레트와 동일 값).
    private static readonly Brush ColIdle = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6));
    private static readonly Brush ColTx = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
    private static readonly Brush ColDeny = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
    private static readonly Brush ColOk = new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x6E));
    private static readonly Brush ColBad = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
    private static readonly Brush ColPanelBorder = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C));
    private static readonly Brush ColFgMain = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));

    private readonly AppConfig _cfg;
    private readonly Logger _log;

    private ControlClient? _control;
    private VoiceClient? _voice;
    private AudioEngine? _audio;
    private PttHook? _hook;

    private readonly ObservableCollection<RosterEntry> _roster = new();

    private bool _keyHeld;          // PTT 물리 키가 눌려 있는가
    private bool _granted;          // 서버가 발언권을 줬는가
    private bool _pttSuspended;     // 설정 화면에서 키 캡처 중
    private bool _initializing = true;
    private DateTime _txStart;
    private int _maxTalkSec = 30;
    private int _statsTick;
    private string _currentSpeaker = ""; // 공통 효과음/유저별 음량 전환 추적
    private bool _callActive;            // 통화(서버 연결)가 켜져 있는가

    public MainWindow(AppConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
        InitializeComponent();
        Icon = TrayIcons.ToImageSource(TrayIcons.Idle);
        RosterList.ItemsSource = _roster;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = _cfg.AlwaysOnTop;
        VolumeSlider.Value = _cfg.OutputVolume;
        VolumeLabel.Text = $"{_cfg.OutputVolume}%";
        _initializing = false;

        if (_cfg.LooksConfigured)
        {
            StartServices();
        }
        else
        {
            ConnText.Text = "● 설정이 필요합니다";
            ConnText.Foreground = ColDeny;
            TxText.Text = "오른쪽 위 [설정] 버튼에서 서버 주소와 콜사인을 입력하세요";
        }
        UpdateCallButton();

        // 계측/페일세이프 타이머
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    // ---------- 서비스 수명주기 ----------

    private void StartServices()
    {
        Topmost = _cfg.AlwaysOnTop;

        // 오디오
        try
        {
            _audio = new AudioEngine(_log);
            _audio.LogDevices();
            _audio.Start(_cfg.InputDevice, _cfg.OutputDevice, _cfg.OutputVolume);
            _audio.SetMicGain(_cfg.MicGain);
        }
        catch (Exception ex)
        {
            _log.Info($"[오디오] 초기화 실패: {ex}");
            _audio?.Dispose();
            _audio = null;
            TxText.Text = $"오디오 장치 오류: {ex.Message}";
            TxText.Foreground = ColBad;
        }

        // 음성(UDP)
        _voice = new VoiceClient(_log);
        if (_audio != null)
        {
            var audio = _audio;
            var voice = _voice;
            audio.EncodedFrame += (buf, len) => voice.SendVoice(buf, len);
            voice.VoiceFrameReceived += (sender, opus) => audio.PlayVoice(sender, opus);
        }

        // 제어(WebSocket)
        _control = new ControlClient(_cfg, _log);
        _control.ConnectionChanged += ok => Dispatcher.BeginInvoke(() => OnConnectionChanged(ok));
        _control.MessageReceived += msg => Dispatcher.BeginInvoke(() => OnControlMessage(msg));
        _control.Start();
        ConnText.Text = $"● {_cfg.ServerHost} 연결 중…";
        ConnText.Foreground = ColIdle;

        // PTT 전역 훅 (UI 스레드 메시지 루프에서 콜백됨)
        try
        {
            _hook = new PttHook(_cfg.PttKey);
            _hook.PttDown += OnPttDown;
            _hook.PttUp += OnPttUp;
            _hook.Install();
            if (_audio != null) // 오디오 오류 메시지를 덮어쓰지 않는다
            {
                TxText.Text = $"대기 — PTT: {_hook.KeyLabel}";
                TxText.Foreground = ColIdle;
            }
        }
        catch (Exception ex)
        {
            _log.Info($"[PTT] 훅 설치 실패: {ex}");
            TxText.Text = "PTT 키 오류 — 설정에서 키를 다시 지정하세요";
            TxText.Foreground = ColBad;
        }

        _callActive = true;
        UpdateCallButton();
    }

    private void StopServices()
    {
        if (_granted) StopTx(sendStop: true, "대기");
        _keyHeld = false;
        _hook?.Dispose(); _hook = null;
        _control?.Dispose(); _control = null;
        _voice?.Dispose(); _voice = null;
        _audio?.Dispose(); _audio = null;
        _roster.Clear();
        _currentSpeaker = "";
        SetSpeaker("");
        _callActive = false;
        UpdateCallButton();
    }

    /// <summary>설정 저장 후 새 설정으로 전체 재시작.</summary>
    public void RestartServices()
    {
        StopServices();
        if (_cfg.LooksConfigured) StartServices();
        VolumeSlider.Value = _cfg.OutputVolume;
    }

    // ---------- 설정 화면과의 연동 ----------

    public float MicLevel => _audio?.MicLevel ?? 0f;

    public void PlayTestBeep() => _audio?.GrantBeep();

    /// <summary>설정 화면 미리듣기용 즉시 음량 변경 (저장과 무관).</summary>
    public void PreviewVolume(int percent) => _audio?.SetVolume(percent);

    /// <summary>설정 화면 미리듣기용 즉시 마이크 증폭 변경 (저장과 무관).</summary>
    public void PreviewMicGain(int percent) => _audio?.SetMicGain(percent);

    // ---------- 통화(서버 연결) 토글 ----------

    private void OnToggleCall(object sender, RoutedEventArgs e)
    {
        if (_callActive)
        {
            StopServices();
            ConnText.Text = "● 통화 종료됨";
            ConnText.Foreground = ColIdle;
            TxText.Text = "통화가 종료되었습니다 — [통화 시작]을 누르면 다시 연결합니다";
            TxText.Foreground = ColIdle;
        }
        else if (!_cfg.LooksConfigured)
        {
            OpenSettings();
        }
        else
        {
            StartServices();
        }
    }

    private void UpdateCallButton()
    {
        if (CallBtn == null) return; // InitializeComponent 이전 호출 방지
        CallBtn.Content = _callActive ? "통화 종료" : "통화 시작";
    }

    /// <summary>키 캡처 중 PTT 오작동 방지.</summary>
    public void SuspendPtt(bool suspend)
    {
        _pttSuspended = suspend;
        if (suspend && _granted) StopTx(sendStop: true, "대기");
    }

    public void OpenSettings()
    {
        var dlg = new SettingsWindow(this, _cfg) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _log.Info("[설정] 저장됨 — 서비스 재시작");
            RestartServices();
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || VolumeLabel == null) return;
        int v = (int)e.NewValue;
        VolumeLabel.Text = $"{v}%";
        _audio?.SetVolume(v);
        if (_cfg.OutputVolume != v)
        {
            _cfg.OutputVolume = v;
            try { _cfg.Save(); } catch { /* 드래그 중 일시적 실패 무시 */ }
        }
    }

    // ---------- PTT 상태 머신 (UI 스레드) ----------

    private void OnPttDown()
    {
        if (_pttSuspended) return;
        if (_control is not { IsConnected: true })
        {
            _audio?.DenyBeep();
            TxText.Text = "서버에 연결되어 있지 않습니다";
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
                ConnText.Text = "● 인증 실패 — 설정에서 토큰을 확인하세요";
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
                SpeakerPanel.BorderBrush = ColTx;
                App.SetTray(TrayIcons.Tx, "VOLT 무전기 — 송신 중");
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
                    StopTx(sendStop: false, "발언권이 강제 회수되었습니다");
                    _audio?.DenyBeep();
                    _log.Info($"[PTT] 서버가 발언권 강제 회수: {msg.Reason}");
                }
                break;

            case "speaker":
                OnSpeakerChanged(msg.Nick ?? "");
                break;

            case "roster":
                UpdateRoster(msg.Users ?? Array.Empty<string>());
                break;
        }
    }

    /// <summary>
    /// 서버의 발언자 전환을 처리한다: 전원 공통 효과음(시작/종료)을 울리고,
    /// 새 발언자의 유저별 수신 음량을 재생 게인에 반영한 뒤 UI를 갱신한다.
    /// </summary>
    private void OnSpeakerChanged(string nick)
    {
        if (nick != _currentSpeaker)
        {
            if (_cfg.RogerBeep)
            {
                if (!string.IsNullOrEmpty(nick)) _audio?.RogerStartBeep();
                else if (!string.IsNullOrEmpty(_currentSpeaker)) _audio?.RogerEndBeep();
            }
            _currentSpeaker = nick;
        }

        // 새 발언자의 유저별 음량을 재생 게인에 적용 (없으면 100%).
        if (_audio != null && !string.IsNullOrEmpty(nick))
            _audio.PlaybackGain = _cfg.UserVolumes.GetValueOrDefault(nick, 100) / 100f;

        SetSpeaker(nick);
    }

    private void SetSpeaker(string nick)
    {
        bool someone = !string.IsNullOrEmpty(nick);
        SpeakerText.Text = someone ? nick : "—";
        SpeakerText.Foreground = someone ? ColOk : ColFgMain;
        foreach (var entry in _roster)
            entry.IsSpeaking = someone && entry.Nick == nick;

        if (_granted) return; // 내가 송신 중이면 Tx 상태 유지 (테두리/트레이)
        SpeakerPanel.BorderBrush = someone ? ColOk : ColPanelBorder;
        App.SetTray(someone ? TrayIcons.Rx : TrayIcons.Idle,
            someone ? $"VOLT 무전기 — {nick} 발언 중" : "VOLT 무전기");
    }

    private void UpdateRoster(string[] users)
    {
        var speaking = _roster.Where(r => r.IsSpeaking).Select(r => r.Nick).ToHashSet();
        foreach (var old in _roster) old.PropertyChanged -= OnRosterEntryChanged;
        _roster.Clear();
        foreach (var u in users.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
        {
            // 초기 Volume 을 구독 전에 설정해 시드 단계에서 저장이 트리거되지 않게 한다.
            var entry = new RosterEntry
            {
                Nick = u,
                IsSpeaking = speaking.Contains(u),
                Volume = _cfg.UserVolumes.GetValueOrDefault(u, 100),
            };
            entry.PropertyChanged += OnRosterEntryChanged;
            _roster.Add(entry);
        }
        RosterCaption.Text = users.Length > 0 ? $"접속자 {users.Length}명" : "접속자";
    }

    /// <summary>접속자별 음량 슬라이더 변경 → config 저장 + 현재 발언자면 즉시 게인 반영.</summary>
    private void OnRosterEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RosterEntry.Volume) || sender is not RosterEntry entry) return;
        _cfg.UserVolumes[entry.Nick] = entry.Volume;
        try { _cfg.Save(); } catch { /* 드래그 중 일시적 실패 무시 */ }
        if (_audio != null && entry.Nick == _currentSpeaker)
            _audio.PlaybackGain = entry.Volume / 100f;
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            ConnText.Text = $"● {_cfg.ServerHost} 연결됨";
            ConnText.Foreground = ColOk;
            TxText.Text = $"대기 — PTT: {_hook?.KeyLabel ?? PttHook.LabelFor(_cfg.PttKey)}";
            TxText.Foreground = ColIdle;
        }
        else
        {
            ConnText.Text = "● 연결 끊김 — 자동 재접속 중…";
            ConnText.Foreground = ColBad;
            if (_granted) StopTx(sendStop: false, "대기");
            SetSpeaker("");
            _roster.Clear();
            RosterCaption.Text = "접속자";
        }
    }

    private void StopTx(bool sendStop, string statusText)
    {
        _granted = false;
        if (_audio != null) _audio.Transmitting = false;
        if (sendStop) _control?.Send("ptt_stop");
        TxText.Text = $"{statusText} — PTT: {_hook?.KeyLabel ?? PttHook.LabelFor(_cfg.PttKey)}";
        TxText.Foreground = ColIdle;
        SpeakerPanel.BorderBrush = ColPanelBorder;
        App.SetTray(TrayIcons.Idle, "VOLT 무전기");
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
        if (_control is not { IsConnected: true })
        {
            StatsText.Text = "";
            return;
        }
        var rtt = _control.LastRttMs;
        var (rx, lost) = _voice?.RxStats ?? (0, 0);
        double lossPct = rx + lost > 0 ? lost * 100.0 / (rx + lost) : 0;
        StatsText.Text = rtt >= 0
            ? $"지연 {rtt:F0} ms · 수신 손실 {lossPct:F1}%"
            : "";

        if (_statsTick % 20 == 0) // 10초마다 로그
            _log.Info($"[계측] RTT {rtt:F1}ms, 수신 {rx}패킷, 손실 {lost} ({lossPct:F1}%), 송신 {_voice?.SentCount ?? 0}패킷");
    }

    // ---------- 창 동작: 닫기 → 트레이로 최소화 또는 종료 (설정에 따름) ----------

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_cfg.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // 트레이로 최소화 옵션이 꺼져 있으면 앱 전체를 종료한다.
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopServices();
        base.OnClosed(e);
    }
}
