using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace VoltComms.Core;

/// <summary>
/// 마이크 캡처 → Opus 인코딩(32kbps/20ms), 수신 Opus → 디코딩 → 재생.
/// 마이크는 항상 캡처해 두고 Transmitting 일 때만 인코딩/송신해
/// PTT 누른 순간의 시동 지연을 없앤다.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000; // 960
    private const int MaxDecodedSamples = SampleRate * 120 / 1000; // Opus 최대 프레임 120ms

    private readonly Logger _log;
    private readonly IOpusEncoder _encoder;
    private IOpusDecoder _decoder;
    private uint _decoderSender;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playBuf;

    private readonly short[] _frame = new short[FrameSamples];
    private int _frameFill;
    private readonly byte[] _encBuf = new byte[1275]; // Opus 최대 패킷 크기
    private readonly short[] _decBuf = new short[MaxDecodedSamples];
    private readonly byte[] _decBytes = new byte[MaxDecodedSamples * 2];

    /// <summary>송신 게이트. true 인 동안만 EncodedFrame 이 발생한다.</summary>
    public volatile bool Transmitting;

    /// <summary>인코딩된 20ms Opus 프레임 — 오디오 스레드에서 호출됨.</summary>
    public event Action<byte[], int>? EncodedFrame;

    public AudioEngine(Logger log)
    {
        _log = log;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 32000;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    }

    public void Start(string inputDeviceName, string outputDeviceName)
    {
        int inDev = FindInputDevice(inputDeviceName);
        int outDev = FindOutputDevice(outputDeviceName);

        _playBuf = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        _waveOut = new WaveOutEvent { DeviceNumber = outDev, DesiredLatency = 100 };
        _waveOut.Init(_playBuf);
        _waveOut.Play();

        _waveIn = new WaveInEvent
        {
            DeviceNumber = inDev,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = FrameMs,
            NumberOfBuffers = 4,
        };
        _waveIn.DataAvailable += OnMicData;
        _waveIn.StartRecording();

        _log.Info($"[오디오] 시작 — 입력: {DeviceLabel(inDev, isInput: true)}, 출력: {DeviceLabel(outDev, isInput: false)}");
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (!Transmitting)
        {
            _frameFill = 0; // 송신 중이 아니면 버린다.
            return;
        }
        // 16비트 샘플을 20ms(960샘플) 프레임으로 모아 인코딩한다.
        int sampleCount = e.BytesRecorded / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            _frame[_frameFill++] = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            if (_frameFill < FrameSamples) continue;
            _frameFill = 0;
            try
            {
                int len = _encoder.Encode(_frame, FrameSamples, _encBuf, _encBuf.Length);
                if (len > 0) EncodedFrame?.Invoke(_encBuf, len);
            }
            catch (Exception ex)
            {
                _log.Info($"[오디오] 인코딩 오류: {ex.Message}");
            }
        }
    }

    /// <summary>수신 Opus 프레임을 디코딩해 재생 버퍼에 넣는다.</summary>
    public void PlayVoice(uint senderId, byte[] opus)
    {
        var playBuf = _playBuf;
        if (playBuf == null) return;
        try
        {
            // 발언자가 바뀌면 디코더 상태를 새로 만든다.
            if (senderId != _decoderSender)
            {
                _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
                _decoderSender = senderId;
            }
            int samples = _decoder.Decode(opus, _decBuf, MaxDecodedSamples, false);
            if (samples <= 0) return;
            for (int i = 0; i < samples; i++)
            {
                _decBytes[i * 2] = (byte)_decBuf[i];
                _decBytes[i * 2 + 1] = (byte)(_decBuf[i] >> 8);
            }
            playBuf.AddSamples(_decBytes, 0, samples * 2);
        }
        catch (Exception ex)
        {
            _log.Info($"[오디오] 디코딩 오류: {ex.Message}");
        }
    }

    /// <summary>발언권 거부 시: 낮은 2연속 버즈.</summary>
    public void DenyBeep()
    {
        AddTone(220, 110);
        AddTone(165, 140);
    }

    /// <summary>발언권 획득 시: 짧은 확인음.</summary>
    public void GrantBeep() => AddTone(880, 70);

    private void AddTone(double freq, int ms, double amplitude = 0.22)
    {
        var playBuf = _playBuf;
        if (playBuf == null) return;
        int n = SampleRate * ms / 1000;
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            // 양 끝 5ms 페이드로 클릭 잡음 방지
            double env = Math.Min(1.0, Math.Min(i, n - i) / (SampleRate * 0.005));
            short s = (short)(Math.Sin(2 * Math.PI * freq * i / SampleRate) * amplitude * env * short.MaxValue);
            bytes[i * 2] = (byte)s;
            bytes[i * 2 + 1] = (byte)(s >> 8);
        }
        playBuf.AddSamples(bytes, 0, bytes.Length);
    }

    private int FindInputDevice(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment)) return -1; // 기본 장치
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            if (WaveInEvent.GetCapabilities(i).ProductName
                .Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        _log.Info($"[오디오] 입력 장치 '{nameFragment}' 를 찾지 못해 기본 장치 사용");
        return -1;
    }

    private int FindOutputDevice(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment)) return -1;
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            if (WaveOut.GetCapabilities(i).ProductName
                .Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        _log.Info($"[오디오] 출력 장치 '{nameFragment}' 를 찾지 못해 기본 장치 사용");
        return -1;
    }

    private static string DeviceLabel(int dev, bool isInput)
    {
        if (dev < 0) return "기본 장치";
        return isInput
            ? WaveInEvent.GetCapabilities(dev).ProductName
            : WaveOut.GetCapabilities(dev).ProductName;
    }

    /// <summary>사용 가능한 장치 목록을 로그로 남긴다 (설정에 쓸 이름 확인용).</summary>
    public void LogDevices()
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            _log.Info($"[오디오] 입력 장치 #{i}: {WaveInEvent.GetCapabilities(i).ProductName}");
        for (int i = 0; i < WaveOut.DeviceCount; i++)
            _log.Info($"[오디오] 출력 장치 #{i}: {WaveOut.GetCapabilities(i).ProductName}");
    }

    public void Dispose()
    {
        Transmitting = false;
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnMicData;
            _waveIn.StopRecording();
            _waveIn.Dispose();
        }
        _waveOut?.Stop();
        _waveOut?.Dispose();
    }
}
