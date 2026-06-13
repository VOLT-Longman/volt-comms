using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VoltComms;

/// <summary>
/// 실행 중에 그려서 만드는 앱/트레이 아이콘. VOLT 날개 로고를 어두운 코인 위에 얹고,
/// 상태(대기/수신/송신)는 우하단 점 색으로 구분한다. 32px에서도 로고가 뭉개지지 않도록
/// 상태색으로 배경을 덮지 않고 점으로만 표시한다.
/// </summary>
internal static class TrayIcons
{
    // 자산이 먼저 초기화되도록 Idle/Rx/Tx 보다 위에 선언한다.
    private static readonly Bitmap? Logo = LoadLogo();

    // 브랜드 색 (volt-website 디자인 토큰과 동기화)
    private static readonly Color Coin = Color.FromArgb(255, 28, 28, 30);    // #1C1C1E
    private static readonly Color RxDot = Color.FromArgb(255, 76, 201, 110); // 수신: 그린
    private static readonly Color TxDot = Color.FromArgb(255, 232, 72, 47);  // 송신: VOLT 오렌지

    public static readonly Icon Idle = Create(null);
    public static readonly Icon Rx = Create(RxDot);
    public static readonly Icon Tx = Create(TxDot);

    /// <summary>날개 로고를 어두운 코인 위에 그린다. dot 이 지정되면 우하단에 상태 점을 찍는다.</summary>
    private static Icon Create(Color? dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var coin = new SolidBrush(Coin))
                g.FillEllipse(coin, 1, 1, 30, 30);

            if (Logo != null)
            {
                // 로고를 코인 안쪽에 맞춰 축소 배치 (가장자리 여백 확보)
                g.DrawImage(Logo, new Rectangle(4, 4, 24, 24));
            }
            else
            {
                // 자산 로드 실패 시 글자 'V' 로 폴백
                using var font = new Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var fg = new SolidBrush(Color.FromArgb(255, 232, 72, 47));
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString("V", font, fg, new RectangleF(0, 1, 32, 30), fmt);
            }

            if (dot is { } c)
            {
                // 상태 점: 어두운 테두리로 코인과 분리
                using var ring = new SolidBrush(Coin);
                g.FillEllipse(ring, 19, 19, 12, 12);
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, 21, 21, 9, 9);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/VOLT_logo.png");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                    return new Bitmap(stream);
            }
        }
        catch
        {
            // 디자이너/초기화 시점 등 리소스 접근 불가 시 폴백 그리기를 사용한다.
        }
        return null;
    }

    /// <summary>WPF 창 아이콘용 변환.</summary>
    public static BitmapSource ToImageSource(Icon icon) =>
        Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
}
