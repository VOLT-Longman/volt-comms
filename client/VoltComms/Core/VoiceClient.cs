using System.Net.Sockets;

namespace VoltComms.Core;

/// <summary>
/// 음성 채널(UDP). 핸드셰이크로 서버에 음성 경로를 등록하고,
/// Opus 프레임을 송수신하며 시퀀스 번호로 패킷 손실을 측정한다.
/// 패킷 형식은 server/protocol.go 참조.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    private const byte Magic = 0x56;
    private const byte TypeHandshake = 0x01;
    private const byte TypeVoiceUp = 0x02;
    private const byte TypeVoiceDown = 0x03;
    private const int HeaderLen = 10;

    private readonly Logger _log;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private uint _sessionId;
    private uint _txSeq;
    private volatile bool _udpConfirmed;

    // 수신 통계 (발언자는 한 번에 한 명이므로 단일 추적으로 충분)
    private uint _rxLastSender;
    private uint _rxLastSeq;
    private long _rxCount;
    private long _rxLost;
    public long SentCount { get; private set; }

    /// <summary>(발언자 세션 ID, Opus 페이로드) — 백그라운드 스레드에서 호출됨.</summary>
    public event Action<uint, byte[]>? VoiceFrameReceived;

    public VoiceClient(Logger log) => _log = log;

    public bool UdpConfirmed => _udpConfirmed;

    public (long received, long lost) RxStats
    {
        get { lock (this) return (_rxCount, _rxLost); }
    }

    /// <summary>auth_ok 수신 후 호출. 핸드셰이크를 udp_ok 확인까지 반복 전송한다.</summary>
    public void Start(string host, int port, uint sessionId)
    {
        Stop();
        _sessionId = sessionId;
        _udpConfirmed = false;
        _txSeq = 0;
        _cts = new CancellationTokenSource();
        var udp = new UdpClient();
        udp.Connect(host, port);
        _udp = udp;
        _ = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
        _ = Task.Run(() => HandshakeLoopAsync(udp, _cts.Token));
        _log.Info($"[음성] UDP 시작: {host}:{port} (세션 {sessionId})");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _udpConfirmed = false;
    }

    /// <summary>제어 채널에서 udp_ok 를 받으면 호출해 핸드셰이크 반복을 멈춘다.</summary>
    public void ConfirmUdp()
    {
        if (!_udpConfirmed)
        {
            _udpConfirmed = true;
            _log.Info("[음성] 서버가 UDP 경로를 확인함");
        }
    }

    private async Task HandshakeLoopAsync(UdpClient udp, CancellationToken ct)
    {
        var pkt = new byte[6];
        pkt[0] = Magic;
        pkt[1] = TypeHandshake;
        WriteUInt32(pkt, 2, _sessionId);
        while (!ct.IsCancellationRequested && !_udpConfirmed)
        {
            try { await udp.SendAsync(pkt, ct); }
            catch (Exception ex) { _log.Info($"[음성] 핸드셰이크 전송 실패: {ex.Message}"); }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>인코딩된 Opus 프레임 한 개(20ms)를 서버로 보낸다.</summary>
    public void SendVoice(byte[] opus, int length)
    {
        var udp = _udp;
        if (udp == null || !_udpConfirmed) return;
        var pkt = new byte[HeaderLen + length];
        pkt[0] = Magic;
        pkt[1] = TypeVoiceUp;
        WriteUInt32(pkt, 2, _sessionId);
        WriteUInt32(pkt, 6, ++_txSeq);
        Buffer.BlockCopy(opus, 0, pkt, HeaderLen, length);
        try
        {
            udp.Send(pkt, pkt.Length);
            SentCount++;
        }
        catch (Exception ex)
        {
            _log.Info($"[음성] 전송 실패: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] data;
            try
            {
                var result = await udp.ReceiveAsync(ct);
                data = result.Buffer;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (data.Length < HeaderLen || data[0] != Magic || data[1] != TypeVoiceDown)
                continue;

            uint sender = ReadUInt32(data, 2);
            uint seq = ReadUInt32(data, 6);
            TrackLoss(sender, seq);

            var payload = new byte[data.Length - HeaderLen];
            Buffer.BlockCopy(data, HeaderLen, payload, 0, payload.Length);
            VoiceFrameReceived?.Invoke(sender, payload);
        }
    }

    private void TrackLoss(uint sender, uint seq)
    {
        lock (this)
        {
            _rxCount++;
            // 발언자가 바뀌거나 시퀀스가 리셋되면 추적을 새로 시작한다.
            if (sender != _rxLastSender || seq <= _rxLastSeq)
            {
                _rxLastSender = sender;
                _rxLastSeq = seq;
                return;
            }
            _rxLost += seq - _rxLastSeq - 1;
            _rxLastSeq = seq;
        }
    }

    private static void WriteUInt32(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8);
        buf[offset + 3] = (byte)v;
    }

    private static uint ReadUInt32(byte[] buf, int offset) =>
        ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
        ((uint)buf[offset + 2] << 8) | buf[offset + 3];

    public void Dispose() => Stop();
}
