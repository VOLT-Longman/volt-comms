package main

// Msg 는 제어 채널(WebSocket)에서 쓰는 단일 JSON 메시지 형식이다.
// type 값에 따라 사용하는 필드가 달라진다.
//
// 클라이언트 → 서버:
//
//	auth       {token, nick}
//	ptt_start  {}
//	ptt_stop   {}
//	ping       {ts}            — 하트비트 겸 RTT 측정
//
// 서버 → 클라이언트:
//
//	auth_ok           {session_id, udp_port, heartbeat_sec, max_talk_sec}
//	auth_fail         {reason}
//	udp_ok            {}        — UDP 핸드셰이크 수신 확인
//	ptt_granted       {}
//	ptt_denied        {holder}
//	ptt_force_release {reason}  — 30초 초과/하트비트 끊김 등으로 강제 회수
//	speaker           {nick, session_id} — 현재 발언자 (nick=="" 이면 없음)
//	roster            {users}
//	pong              {ts}
type Msg struct {
	Type         string   `json:"type"`
	Token        string   `json:"token,omitempty"`
	Nick         string   `json:"nick,omitempty"`
	TS           int64    `json:"ts,omitempty"`
	SessionID    uint32   `json:"session_id,omitempty"`
	UDPPort      int      `json:"udp_port,omitempty"`
	HeartbeatSec int      `json:"heartbeat_sec,omitempty"`
	MaxTalkSec   int      `json:"max_talk_sec,omitempty"`
	Holder       string   `json:"holder,omitempty"`
	Reason       string   `json:"reason,omitempty"`
	Users        []string `json:"users,omitempty"`
}

// UDP 패킷 형식 (바이너리, 빅엔디안):
//
//	[0] magic 'V' (0x56)
//	[1] 패킷 종류: 0x01 핸드셰이크(C→S), 0x02 음성(C→S), 0x03 음성(S→C)
//	[2:6] session_id uint32
//	[6:10] seq uint32 (음성 패킷만; 손실 측정용)
//	[10:] Opus 페이로드 (음성 패킷만)
const (
	udpMagic         = 0x56
	udpTypeHandshake = 0x01
	udpTypeVoiceUp   = 0x02
	udpTypeVoiceDown = 0x03
	udpHeaderLen     = 10
)
