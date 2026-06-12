using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoltComms.Core;

/// <summary>
/// 설정 화면의 "PTT 키 변경"용 일회성 입력 캡처.
/// 다음에 눌리는 키보드 키 또는 마우스 버튼(사이드/휠클릭) 하나를 잡아낸다.
/// ESC 는 취소, 좌/우 클릭은 UI 조작을 위해 무시한다.
/// </summary>
public sealed class InputCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int VK_ESCAPE = 0x1B;

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

    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;
    private IntPtr _kbHook;
    private IntPtr _mouseHook;
    private bool _done;

    /// <summary>잡힌 키 이름(설정 파일에 저장하는 형식). null 이면 ESC 로 취소.</summary>
    public event Action<string?>? Captured;

    public InputCapture()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        var hMod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    private void Finish(string? result)
    {
        if (_done) return;
        _done = true;
        Captured?.Invoke(result);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_done)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                Finish(info.vkCode == VK_ESCAPE ? null : ((Keys)info.vkCode).ToString());
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_done)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_MBUTTONDOWN)
            {
                Finish("MButton");
            }
            else if (msg == WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Finish((info.mouseData >> 16) == 1 ? "XButton1" : "XButton2");
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _kbHook = _mouseHook = IntPtr.Zero;
    }
}
