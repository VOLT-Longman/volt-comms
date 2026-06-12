using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VoltComms;

/// <summary>
/// 실행 중에 그려서 만드는 앱 아이콘. 파일을 따로 배포하지 않아도 되고,
/// 상태(대기/수신/송신)에 따라 트레이 색을 바꾼다.
/// </summary>
internal static class TrayIcons
{
    public static readonly Icon Idle = Create(Color.FromArgb(38, 44, 52), Color.FromArgb(232, 185, 49));
    public static readonly Icon Rx = Create(Color.FromArgb(46, 140, 80), Color.White);
    public static readonly Icon Tx = Create(Color.FromArgb(206, 58, 58), Color.White);

    private static Icon Create(Color bg, Color fg)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bgBrush = new SolidBrush(bg);
            g.FillEllipse(bgBrush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var fgBrush = new SolidBrush(fg);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("V", font, fgBrush, new RectangleF(0, 1, 32, 30), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>WPF 창 아이콘용 변환.</summary>
    public static BitmapSource ToImageSource(Icon icon) =>
        Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
}
