using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoltComms.Core;

/// <summary>
/// 서버와의 제어 채널. 자동 재접속, 인증, 하트비트(ping/RTT 측정)를 담당한다.
/// 이벤트는 백그라운드 스레드에서 올라오므로 UI 갱신은 Dispatcher 를 거쳐야 한다.
/// </summary>
public sealed class ControlClient : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Logger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _heartbeatSec = 5;

    public bool IsConnected { get; private set; }
    public double LastRttMs { get; private set; } = -1;

    public event Action<bool>? ConnectionChanged;
    public event Action<ControlMessage>? MessageReceived;

    public ControlClient(AppConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var scheme = _cfg.UseTls ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{_cfg.ServerHost}:{_cfg.ServerPort}/ws");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(uri, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Info($"[제어] 연결 실패/끊김: {ex.Message}");
            }
            if (IsConnected)
            {
                IsConnected = false;
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task SessionAsync(Uri uri, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _log.Info($"[제어] 접속 시도: {uri}");
        await ws.ConnectAsync(uri, ct);
        _ws = ws;

        await SendAsync(new ControlMessage { Type = "auth", Token = _cfg.Token, Nick = _cfg.Nickname }, ct);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(pingCts.Token);

        try
        {
            var buf = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("서버가 연결을 닫음");
                    sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                } while (!result.EndOfMessage);

                var msg = JsonSerializer.Deserialize<ControlMessage>(sb.ToString());
                if (msg == null) continue;
                HandleMessage(msg);
            }
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { /* 무시 */ }
            _ws = null;
        }
    }

    private void HandleMessage(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "auth_ok":
                IsConnected = true;
                if (msg.HeartbeatSec > 0) _heartbeatSec = msg.HeartbeatSec;
                _log.Info($"[제어] 인증 성공 (세션 {msg.SessionId}, UDP 포트 {msg.UdpPort})");
                ConnectionChanged?.Invoke(true);
                break;
            case "auth_fail":
                _log.Info($"[제어] 인증 실패: {msg.Reason}");
                break;
            case "pong":
                LastRttMs = (Stopwatch.GetTimestamp() - msg.Ts) * 1000.0 / Stopwatch.Frequency;
                break;
        }
        MessageReceived?.Invoke(msg);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSec), ct);
            await SendAsync(new ControlMessage { Type = "ping", Ts = Stopwatch.GetTimestamp() }, ct);
        }
    }

    /// <summary>비동기 전송. 연결이 없으면 조용히 무시한다.</summary>
    public void Send(string type) =>
        _ = SendSafeAsync(new ControlMessage { Type = type });

    private async Task SendSafeAsync(ControlMessage msg)
    {
        try { await SendAsync(msg, _cts.Token); }
        catch (Exception ex) { _log.Info($"[제어] 전송 실패({msg.Type}): {ex.Message}"); }
    }

    private async Task SendAsync(ControlMessage msg, CancellationToken ct)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var data = JsonSerializer.SerializeToUtf8Bytes(msg);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ws?.Abort();
    }
}
