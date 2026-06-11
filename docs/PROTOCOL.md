# VOLT Comms 프로토콜 (1단계)

서버 구현: `server/`, 클라이언트 구현: `client/VoltComms/Core/`.

## 제어 채널 — WebSocket `/ws` (JSON)

연결 후 **첫 메시지는 반드시 `auth`** 여야 하며, 10초 안에 오지 않으면 서버가 연결을 끊는다.

### 클라이언트 → 서버

| type | 필드 | 설명 |
|---|---|---|
| `auth` | `token`, `nick` | 공유 토큰 인증 |
| `ptt_start` | — | 발언권 요청 |
| `ptt_stop` | — | 발언권 반납 |
| `ping` | `ts` | 하트비트(5초 주기) 겸 RTT 측정. `ts`는 클라이언트 로컬 타임스탬프로, 서버는 그대로 되돌려준다 |

### 서버 → 클라이언트

| type | 필드 | 설명 |
|---|---|---|
| `auth_ok` | `session_id`, `udp_port`, `heartbeat_sec`, `max_talk_sec` | 인증 성공. 이후 UDP 핸드셰이크 시작 |
| `auth_fail` | `reason` | 인증 실패 (연결 종료됨) |
| `udp_ok` | — | UDP 핸드셰이크 수신 확인 (이후 핸드셰이크 재전송 중단) |
| `ptt_granted` | — | 발언권 부여 |
| `ptt_denied` | `holder` | 다른 사람이 점유 중 → 거부 비프음 재생 |
| `ptt_force_release` | `reason` | 강제 회수: `max_talk_exceeded`(30초 초과) 또는 `heartbeat_lost` |
| `speaker` | `nick`, `session_id` | 현재 발언자 브로드캐스트. `nick==""` 이면 발언자 없음 |
| `roster` | `users` | 접속자 닉네임 목록 |
| `pong` | `ts` | `ping` 응답 (RTT = 현재시각 − ts) |

### 발언권 규칙 (서버 중재)

- 발언권은 전역 1개. 비어 있을 때 `ptt_start` → `ptt_granted`, 점유 중이면 `ptt_denied`.
- 서버 워치독(1초 주기)이 다음 경우 강제 회수 후 `ptt_force_release` 통보:
  - 연속 발언이 `max-talk`(기본 30초) 초과
  - 보유자의 하트비트가 `heartbeat-timeout`(기본 12초) 이상 끊김
- 보유자의 WebSocket이 끊기면 즉시 회수.
- 클라이언트도 동일한 30초 페일세이프를 자체 실행한다(이중 안전장치).

## 음성 채널 — UDP (바이너리, 빅엔디안)

```
[0]    매직 0x56 ('V')
[1]    종류: 0x01 핸드셰이크(C→S) | 0x02 음성(C→S) | 0x03 음성(S→C)
[2:6]  session_id (uint32) — auth_ok 로 받은 값
[6:10] seq (uint32, 음성만) — 패킷 손실 측정용 송신 일련번호
[10:]  Opus 페이로드 (음성만; 48kHz 모노, 32kbps, 20ms 프레임)
```

- **핸드셰이크**: 인증 후 클라이언트가 0.5초 간격으로 전송. 서버는 출발지 주소를
  `session_id`에 매핑하고 제어 채널로 `udp_ok`를 보낸다. (NAT 바인딩 유지 겸용)
- **상향 음성(0x02)**: 서버는 ①등록된 주소·세션 일치 ②현재 발언권 보유자인지 검증 후,
  종류 바이트를 0x03으로 바꿔 **나머지 전원에게** 중계한다. 발언권이 없는 송신은 버린다.
- **손실 측정**: 수신 측이 seq 간격으로 계산해 UI 표시 및 10초마다 로그 기록.

## 보안 메모 (1단계 한계)

- 토큰은 함대 공유 비밀 1개. 제어 채널은 TLS(`-tls-cert`/`-tls-key`) 권장.
- UDP 음성은 1단계에서 암호화하지 않는다(Opus 바이너리). 2단계에서 세션키 암호화 예정.
