using System.Windows;
using System.Windows.Threading;
using VoltComms.Core;

namespace VoltComms;

/// <summary>
/// 설정 화면. 저장하면 config.json 에 쓰고 MainWindow 가 서비스를 재시작해
/// 즉시 적용한다 (앱 재실행 불필요).
/// </summary>
public partial class SettingsWindow : Window
{
    private const string DefaultDevice = "기본 장치 (윈도우 설정)";

    private readonly AppConfig _cfg;
    private readonly MainWindow _main;
    private string _pttKey;
    private InputCapture? _capture;
    private readonly DispatcherTimer _levelTimer;

    public SettingsWindow(MainWindow main, AppConfig cfg)
    {
        _main = main;
        _cfg = cfg;
        _pttKey = cfg.PttKey;
        InitializeComponent();
        Icon = TrayIcons.ToImageSource(TrayIcons.Idle);

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _levelTimer.Tick += (_, _) => LevelBar.Value = _main.MicLevel * 100;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HostBox.Text = _cfg.ServerHost;
        PortBox.Text = _cfg.ServerPort.ToString();
        TokenBox.Text = _cfg.Token;
        NickBox.Text = _cfg.Nickname;
        TlsCheck.IsChecked = _cfg.UseTls;
        UpdatePttLabel();

        FillDeviceCombo(InputCombo, AudioEngine.InputDeviceNames(), _cfg.InputDevice);
        FillDeviceCombo(OutputCombo, AudioEngine.OutputDeviceNames(), _cfg.OutputDevice);

        VolumeSlider.Value = _cfg.OutputVolume;
        MicGainSlider.Value = _cfg.MicGain;
        MicGainLabel.Text = $"{_cfg.MicGain}%";
        GrantBeepCheck.IsChecked = _cfg.GrantBeep;
        RogerBeepCheck.IsChecked = _cfg.RogerBeep;
        CloseToTrayCheck.IsChecked = _cfg.CloseToTray;
        TopmostCheck.IsChecked = _cfg.AlwaysOnTop;
        MinimizedCheck.IsChecked = _cfg.StartMinimized;

        _levelTimer.Start();
    }

    private static void FillDeviceCombo(System.Windows.Controls.ComboBox combo, List<string> devices, string current)
    {
        combo.Items.Clear();
        combo.Items.Add(DefaultDevice);
        foreach (var d in devices) combo.Items.Add(d);
        combo.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(current))
        {
            for (int i = 1; i < combo.Items.Count; i++)
            {
                if (((string)combo.Items[i]).Contains(current, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static string SelectedDevice(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedIndex <= 0 ? "" : (string)combo.SelectedItem;

    private void UpdatePttLabel() => PttKeyText.Text = PttHook.LabelFor(_pttKey);

    // ---------- PTT 키 캡처 ----------

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capture != null) return; // 이미 대기 중
        PttKeyText.Text = "원하는 키/마우스 버튼을 누르세요… (ESC 취소)";
        CaptureBtn.IsEnabled = false;
        _main.SuspendPtt(true); // 캡처 중 실수로 송신되지 않게 잠시 정지

        _capture = new InputCapture();
        _capture.Captured += name => Dispatcher.BeginInvoke(() =>
        {
            if (name != null) _pttKey = name;
            UpdatePttLabel();
            CaptureBtn.IsEnabled = true;
            _capture?.Dispose();
            _capture = null;
            _main.SuspendPtt(false);
        });
        _capture.Install();
    }

    // ---------- 오디오 ----------

    private void OnTestClick(object sender, RoutedEventArgs e) => _main.PlayTestBeep();

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel == null) return; // 초기화 전
        VolumeLabel.Text = $"{(int)e.NewValue}%";
        _main.PreviewVolume((int)e.NewValue); // 즉시 들리는 음량으로 반영
    }

    private void OnMicGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicGainLabel == null) return; // 초기화 전
        MicGainLabel.Text = $"{(int)e.NewValue}%";
        _main.PreviewMicGain((int)e.NewValue); // 즉시 증폭 반영
    }

    // ---------- 저장/취소 ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var host = HostBox.Text.Trim();
        if (host.Length == 0) { ShowError("서버 주소를 입력하세요."); return; }
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        { ShowError("포트는 1~65535 사이 숫자여야 합니다."); return; }
        if (TokenBox.Text.Trim().Length == 0) { ShowError("비밀 토큰을 입력하세요. (서버 운영자에게 받으세요)"); return; }
        if (NickBox.Text.Trim().Length == 0) { ShowError("콜사인(닉네임)을 입력하세요."); return; }

        _cfg.ServerHost = host;
        _cfg.ServerPort = port;
        _cfg.UseTls = TlsCheck.IsChecked == true;
        _cfg.Token = TokenBox.Text.Trim();
        _cfg.Nickname = NickBox.Text.Trim();
        _cfg.PttKey = _pttKey;
        _cfg.InputDevice = SelectedDevice(InputCombo);
        _cfg.OutputDevice = SelectedDevice(OutputCombo);
        _cfg.OutputVolume = (int)VolumeSlider.Value;
        _cfg.MicGain = (int)MicGainSlider.Value;
        _cfg.GrantBeep = GrantBeepCheck.IsChecked == true;
        _cfg.RogerBeep = RogerBeepCheck.IsChecked == true;
        _cfg.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _cfg.AlwaysOnTop = TopmostCheck.IsChecked == true;
        _cfg.StartMinimized = MinimizedCheck.IsChecked == true;

        try
        {
            _cfg.Save();
        }
        catch (Exception ex)
        {
            ShowError($"설정 저장 실패: {ex.Message}");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnClosedWindow(object? sender, EventArgs e)
    {
        _levelTimer.Stop();
        _capture?.Dispose();
        _capture = null;
        _main.SuspendPtt(false);
        _main.PreviewVolume(_cfg.OutputVolume); // 취소 시 원래 음량 복원
        _main.PreviewMicGain(_cfg.MicGain);     // 취소 시 원래 증폭 복원
    }
}
