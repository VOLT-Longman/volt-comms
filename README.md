# VOLT Comms — 함대 지휘망 PTT 무전기

VOLT 함대(스타시티즌) 지휘망(편대장 이상, 동시 8~10명) 전용 반이중(PTT) 음성 통신 앱입니다.
디스코드와 병행 사용하며, **한 번에 한 명만 송신**할 수 있는 무전기 규율을 서버가 강제합니다.

| 구성요소 | 기술 | 배포 형태 |
|---|---|---|
| 클라이언트 | C# / .NET 8 (WPF, 트레이 앱) | `VoltComms.exe` 단일 파일 (설치 불필요) |
| 서버 | Go | `volt-server` 리눅스 단일 바이너리 |

핵심 동작:

- **음성**: Opus 32kbps / 20ms 프레임, UDP 전송
- **제어**: WebSocket(JSON) + 공유 토큰 인증, TLS 지원
- **반이중 중재**: 서버가 발언권 토큰을 관리. 점유 중 타인이 PTT를 누르면 거부 비프음
- **stuck-PTT 페일세이프**: 연속 발언 30초 초과 또는 하트비트 끊김 시 발언권 강제 회수 (서버·클라이언트 양쪽)
- **전역 PTT**: WH_KEYBOARD_LL / WH_MOUSE_LL 저수준 훅 — 게임이 전체화면이어도 동작, 키 입력을 가로채지 않음. 마우스 사이드 버튼(Mouse4/5) 지원
- **계측**: RTT·패킷 손실을 창에 표시하고 `logs/` 폴더에 10초마다 기록

---

## 1. 빌드 결과물 다운로드 (개발 지식 불필요)

코드를 푸시할 때마다 GitHub Actions가 자동으로 빌드합니다.

1. 이 저장소의 GitHub 페이지에서 상단 **Actions** 탭 클릭
2. 왼쪽에서 **build** 워크플로 선택 → 목록 맨 위의 초록색 체크(✓)가 붙은 실행을 클릭
3. 페이지 아래 **Artifacts** 섹션에서 다운로드:
   - **`VoltComms-win-x64`** → 압축 풀면 `VoltComms.exe` (윈도우 클라이언트)
   - **`volt-server-linux-amd64`** → 압축 풀면 `volt-server` (리눅스 서버)

> 나중에 `v0.1.0` 처럼 `v`로 시작하는 태그를 푸시하면 같은 파일이 **Releases** 페이지에도 자동 게시됩니다.

---

## 2. 서버 실행

### 서울 VPS (리눅스)

```bash
# 1) 파일 업로드 후 실행 권한 부여
chmod +x volt-server

# 2) 실행 — 토큰은 함대원에게만 공유할 비밀번호 역할
./volt-server -token "우리함대만아는비밀토큰"
```

기본 포트: 제어 **TCP 8443**, 음성 **UDP 4001**. 방화벽/보안그룹에서 두 포트를 열어야 합니다.

자주 쓰는 옵션:

```bash
./volt-server -h                          # 전체 옵션 보기
./volt-server -token 비밀 -listen :8443 -udp-port 4001
./volt-server -token 비밀 -tls-cert cert.pem -tls-key key.pem   # TLS(wss) 활성화
```

TLS 인증서가 준비되기 전에는 평문(ws)으로 동작합니다. 이때 클라이언트 `use_tls`는 `false`로 두세요.

### 내 PC에서 임시 테스트 (윈도우)

서버는 Go 바이너리라 윈도우용도 만들 수 있지만, 1단계 산출물은 리눅스용입니다.
VPS가 아직 없다면 WSL(우분투)에서 위 명령 그대로 실행하면 됩니다.

---

## 3. 클라이언트 설정과 2인 테스트

1. `VoltComms.exe`를 아무 폴더에 두고 실행 → 옆에 `config.json`이 자동 생성되고 안내창이 뜹니다
2. 앱을 끄고 `config.json`을 메모장으로 열어 수정:

```json
{
  "server_host": "서버주소.example.com",
  "server_port": 8443,
  "use_tls": false,
  "token": "우리함대만아는비밀토큰",
  "nickname": "내콜사인",
  "ptt_key": "XButton2",
  "input_device": "",
  "output_device": "",
  "grant_beep": true,
  "always_on_top": true
}
```

3. 다시 실행 → 창에 **"● 서버주소 연결됨"** 이 초록색으로 표시되면 준비 완료
4. **두 번째 사람**도 같은 방법으로 설정(닉네임만 다르게) 후 접속
5. 테스트 절차:
   - A가 PTT 키를 누른 채 말하기 → B 화면 "현재 발언자"에 A 닉네임이 뜨고 음성이 들림
   - A가 말하는 도중 B가 PTT를 누르면 → B에게 **거부 비프음** + "거부 — A 발언 중" 표시
   - A가 키에서 손을 떼면 발언자 표시가 사라지고, 이제 B가 송신 가능
   - 30초 이상 계속 누르고 있으면 자동으로 끊김(페일세이프) 확인

### config.json 항목 설명

| 항목 | 설명 |
|---|---|
| `server_host` / `server_port` | 서버 주소와 제어 포트 (기본 8443) |
| `use_tls` | 서버가 TLS(wss)로 떠 있으면 `true` |
| `token` | 서버 실행 시 지정한 토큰과 동일해야 접속됨 |
| `nickname` | 다른 사람 화면에 표시될 콜사인 |
| `ptt_key` | PTT 키. 마우스: `"XButton1"`(뒤로), `"XButton2"`(앞으로), 별칭 `"Mouse4"`/`"Mouse5"`. 키보드: `"F13"`, `"Scroll"`, `"Pause"` 등 |
| `input_device` / `output_device` | 장치 이름 일부 (빈칸 = 윈도우 기본 장치). 사용 가능한 장치 이름은 `logs/` 폴더 로그에 기록됨 |
| `grant_beep` | 발언권 획득 시 짧은 확인음 |
| `always_on_top` | 창을 항상 위에 표시 |

> PTT 키는 게임에서 안 쓰는 키를 고르세요. 앱은 키 입력을 가로채지 않으므로(통과시킴) 게임 키와 겹치면 양쪽에서 동시에 동작합니다.

### 문제 해결

- **연결이 안 됨**: 서버 주소/포트/토큰 확인 → 그래도 안 되면 exe 옆 `logs/volt-날짜.log` 파일 확인
- **소리가 안 들림 / 마이크가 안 잡힘**: 로그에 찍힌 장치 목록을 보고 `input_device`/`output_device`에 원하는 장치 이름 일부를 적기
- **연결은 되는데 음성이 안 감**: 서버 방화벽에서 **UDP 4001** 이 열려 있는지 확인 (제어 TCP 8443과 별개)
- 창을 닫으면 종료가 아니라 **트레이로 내려갑니다**. 완전 종료는 트레이 아이콘 우클릭 → 종료

---

## 4. 저장소 구조

```
server/   Go 서버 (발언권 중재 + UDP 음성 릴레이)
client/   .NET 8 WPF 클라이언트 (트레이 앱)
docs/     프로토콜 명세
.github/  자동 빌드 워크플로
```

프로토콜 상세는 [docs/PROTOCOL.md](docs/PROTOCOL.md) 참조.

## 5. 직접 빌드 (선택)

```bash
# 서버 (Go 1.23+)
cd server && go build -o volt-server .

# 클라이언트 (윈도우, .NET 8 SDK)
dotnet publish client/VoltComms/VoltComms.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```
