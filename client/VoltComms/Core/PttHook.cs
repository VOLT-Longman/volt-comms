using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoltComms.Core;

/// <summary>
/// 전역 PTT 입력 감지.
///
/// WH_KEYBOARD_LL / WH_MOUSE_LL 저수준 훅을 사용한다 — RegisterHotKey 는
/// 키 입력을 다른 앱(게임)에서 가로채 버리므로 쓰지 않는다. 저수준 훅은
/// 입력을 관찰만 하고 그대로 통과시키므로(스왈로우하지 않음) 게임이
/// 전체화면이어도 키가 게임과 무전기 양쪽에 모두 전달된다.
///
/// 반드시 메시지 루프가 있는 스레드(WPF UI 스레드)에서 Install() 할 것.
/// </summary>
public sealed class PttHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // GC가 델리게이트를 수거하면 훅이 죽으므로 필드로 붙잡아 둔다.
    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;
    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    private readonly int _vk;          // 키보드 모드일 때 가상 키 코드 (0이면 마우스 모드)
    private readonly int _mouseButton; // 마우스 모드: 1=XButton1, 2=XButton2, 3=휠클릭
    private bool _isDown;

    /// <summary>설정 문자열을 사람이 읽을 이름으로 정규화한 값.</summary>
    public string KeyLabel { get; }

    public event Action? PttDown;
    public event Action? PttUp;

    /// <summary>설정 문자열을 화면에 보여줄 한국어 라벨로 바꾼다 (설정 UI에서 사용).</summary>
    public static string LabelFor(string keyName) =>
        keyName.Trim().ToLowerInvariant() switch
        {
            "xbutton1" or "mouse4" => "마우스4 (뒤로 가기 버튼)",
            "xbutton2" or "mouse5" => "마우스5 (앞으로 가기 버튼)",
            "mbutton" or "mouse3" => "마우스 휠 클릭",
            _ => keyName.Trim(),
        };

    /// <exception cref="ArgumentException">키 이름을 해석할 수 없을 때.</exception>
    public PttHook(string keyName)
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;

        var name = keyName.Trim();
        KeyLabel = LabelFor(name);
        switch (name.ToLowerInvariant())
        {
            case "xbutton1" or "mouse4":
                _mouseButton = 1;
                return;
            case "xbutton2" or "mouse5":
                _mouseButton = 2;
                return;
            case "mbutton" or "mouse3":
                _mouseButton = 3;
                return;
        }
        if (!Enum.TryParse<Keys>(name, ignoreCase: true, out var key) || key == Keys.None)
            throw new ArgumentException($"PTT 키 이름을 해석할 수 없습니다: \"{keyName}\"");
        _vk = (int)key;
    }

    public void Install()
    {
        var hMod = GetModuleHandle(null);
        if (_mouseButton != 0)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero)
                throw new InvalidOperationException($"마우스 훅 설치 실패 (오류 {Marshal.GetLastWin32Error()})");
        }
        else
        {
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero)
                throw new InvalidOperationException($"키보드 훅 설치 실패 (오류 {Marshal.GetLastWin32Error()})");
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == (uint)_vk)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) FireDown();
                else if (msg is WM_KEYUP or WM_SYSKEYUP) FireUp();
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (_mouseButton == 3)
            {
                if (msg == WM_MBUTTONDOWN) FireDown();
                else if (msg == WM_MBUTTONUP) FireUp();
            }
            else if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int button = (int)(info.mouseData >> 16); // HIWORD: 1=XButton1, 2=XButton2
                if (button == _mouseButton)
                {
                    if (msg == WM_XBUTTONDOWN) FireDown();
                    else FireUp();
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void FireDown()
    {
        if (_isDown) return; // 키 반복(오토리핏) 무시
        _isDown = true;
        PttDown?.Invoke();
    }

    private void FireUp()
    {
        if (!_isDown) return;
        _isDown = false;
        PttUp?.Invoke();
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _kbHook = _mouseHook = IntPtr.Zero;
    }
}
