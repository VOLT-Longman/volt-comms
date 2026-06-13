package main

import (
	"encoding/json"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const heartbeatIntervalSec = 5 // 클라이언트가 ping을 보내야 하는 주기 (auth_ok로 통지)

var upgrader = websocket.Upgrader{
	ReadBufferSize:  1024,
	WriteBufferSize: 1024,
	// 전용 클라이언트 앱이므로 Origin 검사는 하지 않는다.
	CheckOrigin: func(r *http.Request) bool { return true },
}

// Hub 는 접속자 목록과 발언권(반이중 토큰)을 관리한다.
type Hub struct {
	token     string
	udpPort   int
	maxTalk   time.Duration
	hbTimeout time.Duration
	relay     *UDPRelay

	mu        sync.Mutex
	clients   map[uint32]*Client
	nextID    uint32
	holder    *Client // 현재 발언권 보유자 (nil이면 비어 있음)
	talkStart time.Time
}

func NewHub(token string, udpPort int, maxTalk, hbTimeout time.Duration) *Hub {
	return &Hub{
		token:     token,
		udpPort:   udpPort,
		maxTalk:   maxTalk,
		hbTimeout: hbTimeout,
		clients:   make(map[uint32]*Client),
		nextID:    1,
	}
}

// ServeWS 는 /ws 로 들어온 연결을 업그레이드하고 클라이언트 세션을 시작한다.
func (h *Hub) ServeWS(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("WS 업그레이드 실패 (%s): %v", r.RemoteAddr, err)
		return
	}
	c := &Client{
		hub:      h,
		conn:     conn,
		send:     make(chan []byte, 64),
		lastPing: time.Now(),
	}
	go c.writePump()
	c.readPump() // 연결이 끊길 때까지 블록
}

func (h *Hub) register(c *Client) {
	h.mu.Lock()
	c.id = h.nextID
	h.nextID++
	h.clients[c.id] = c
	h.mu.Unlock()
	log.Printf("[접속] %s (세션 %d, %s)", c.nick, c.id, c.conn.RemoteAddr())
	h.broadcastRoster()
	// 새 접속자에게 현재 발언자 상태를 알려준다.
	h.mu.Lock()
	holder := h.holder
	h.mu.Unlock()
	if holder != nil {
		c.sendMsg(Msg{Type: "speaker", Nick: holder.nick, SessionID: holder.id})
	}
}

func (h *Hub) unregister(c *Client) {
	h.mu.Lock()
	_, known := h.clients[c.id]
	delete(h.clients, c.id)
	wasHolder := h.holder == c
	if wasHolder {
		h.holder = nil
	}
	h.mu.Unlock()
	if !known {
		return
	}
	if h.relay != nil {
		h.relay.Remove(c.id)
	}
	log.Printf("[퇴장] %s (세션 %d)", c.nick, c.id)
	if wasHolder {
		log.Printf("[발언권] %s 연결 종료로 회수", c.nick)
		h.broadcastSpeaker(nil)
	}
	h.broadcastRoster()
}

// grantPTT 는 발언권을 요청한 클라이언트에게 부여를 시도한다.
func (h *Hub) grantPTT(c *Client) {
	h.mu.Lock()
	if h.holder == nil || h.holder == c {
		alreadyMine := h.holder == c
		h.holder = c
		if !alreadyMine {
			// 새로 부여될 때만 발언 타이머를 시작한다. 이미 보유 중인데
			// ptt_start 가 반복돼도 talkStart 를 리셋하지 않아야 30초
			// 최대 발언 제한을 우회할 수 없다.
			h.talkStart = time.Now()
		}
		h.mu.Unlock()
		c.sendMsg(Msg{Type: "ptt_granted"})
		if !alreadyMine {
			log.Printf("[발언권] %s 획득", c.nick)
			h.broadcastSpeaker(c)
		}
		return
	}
	holderNick := h.holder.nick
	h.mu.Unlock()
	c.sendMsg(Msg{Type: "ptt_denied", Holder: holderNick})
	log.Printf("[발언권] %s 요청 거부 (보유자: %s)", c.nick, holderNick)
}

// releasePTT 는 c가 발언권을 보유 중일 때만 해제한다.
func (h *Hub) releasePTT(c *Client, reason string) {
	h.mu.Lock()
	if h.holder != c {
		h.mu.Unlock()
		return
	}
	held := time.Since(h.talkStart)
	h.holder = nil
	h.mu.Unlock()
	log.Printf("[발언권] %s 해제 (%s, %.1f초)", c.nick, reason, held.Seconds())
	h.broadcastSpeaker(nil)
}

// forceRelease 는 워치독이 호출한다. 보유자에게 강제 회수를 통보한다.
func (h *Hub) forceRelease(c *Client, reason string) {
	h.releasePTT(c, reason)
	c.sendMsg(Msg{Type: "ptt_force_release", Reason: reason})
}

// holderID 는 UDP 릴레이가 음성 중계 허용 여부를 판단할 때 쓴다.
func (h *Hub) holderID() (uint32, bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.holder == nil {
		return 0, false
	}
	return h.holder.id, true
}

// Watchdog 은 stuck-PTT 페일세이프: 최대 발언 시간 초과 또는
// 하트비트 끊김 시 발언권을 강제 회수한다.
func (h *Hub) Watchdog() {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for range ticker.C {
		h.mu.Lock()
		holder := h.holder
		var talking time.Duration
		if holder != nil {
			talking = time.Since(h.talkStart)
		}
		h.mu.Unlock()
		if holder == nil {
			continue
		}
		if talking > h.maxTalk {
			log.Printf("[워치독] %s 최대 발언 시간(%.0f초) 초과 — 강제 회수", holder.nick, h.maxTalk.Seconds())
			h.forceRelease(holder, "max_talk_exceeded")
			continue
		}
		if time.Since(holder.heartbeat()) > h.hbTimeout {
			log.Printf("[워치독] %s 하트비트 끊김 — 강제 회수", holder.nick)
			h.forceRelease(holder, "heartbeat_lost")
		}
	}
}

func (h *Hub) broadcastSpeaker(c *Client) {
	m := Msg{Type: "speaker"}
	if c != nil {
		m.Nick = c.nick
		m.SessionID = c.id
	}
	h.broadcast(m)
}

func (h *Hub) broadcastRoster() {
	h.mu.Lock()
	users := make([]string, 0, len(h.clients))
	for _, c := range h.clients {
		users = append(users, c.nick)
	}
	h.mu.Unlock()
	h.broadcast(Msg{Type: "roster", Users: users})
}

func (h *Hub) broadcast(m Msg) {
	data, err := json.Marshal(m)
	if err != nil {
		return
	}
	h.mu.Lock()
	targets := make([]*Client, 0, len(h.clients))
	for _, c := range h.clients {
		targets = append(targets, c)
	}
	h.mu.Unlock()
	for _, c := range targets {
		c.sendRaw(data)
	}
}
