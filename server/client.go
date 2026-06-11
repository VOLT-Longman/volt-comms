package main

import (
	"encoding/json"
	"log"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	readTimeout  = 30 * time.Second // 이 시간 동안 아무 메시지도 없으면 연결 종료
	writeTimeout = 10 * time.Second
	authTimeout  = 10 * time.Second // 접속 후 인증까지 허용 시간
)

// Client 는 WebSocket으로 연결된 클라이언트 한 명을 나타낸다.
type Client struct {
	hub  *Hub
	conn *websocket.Conn
	id   uint32
	nick string

	send     chan []byte
	sendOnce sync.Once // send 채널 close 중복 방지

	mu       sync.Mutex
	lastPing time.Time
}

func (c *Client) heartbeat() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lastPing
}

func (c *Client) touch() {
	c.mu.Lock()
	c.lastPing = time.Now()
	c.mu.Unlock()
}

func (c *Client) sendMsg(m Msg) {
	data, err := json.Marshal(m)
	if err != nil {
		return
	}
	c.sendRaw(data)
}

func (c *Client) sendRaw(data []byte) {
	select {
	case c.send <- data:
	default:
		// 송신 버퍼가 가득 찬 비정상 클라이언트는 메시지를 버린다.
	}
}

func (c *Client) close() {
	c.sendOnce.Do(func() { close(c.send) })
}

func (c *Client) writePump() {
	defer c.conn.Close()
	for data := range c.send {
		c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
		if err := c.conn.WriteMessage(websocket.TextMessage, data); err != nil {
			return
		}
	}
	c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
	c.conn.WriteMessage(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""))
}

// readPump 은 인증을 처리한 뒤 메시지 루프를 돈다. 연결 종료 시 정리한다.
func (c *Client) readPump() {
	defer func() {
		c.hub.unregister(c)
		c.close()
		c.conn.Close()
	}()

	// 1) 첫 메시지는 반드시 auth.
	c.conn.SetReadDeadline(time.Now().Add(authTimeout))
	_, data, err := c.conn.ReadMessage()
	if err != nil {
		return
	}
	var m Msg
	if err := json.Unmarshal(data, &m); err != nil || m.Type != "auth" {
		c.sendMsg(Msg{Type: "auth_fail", Reason: "first message must be auth"})
		return
	}
	if m.Token != c.hub.token {
		c.sendMsg(Msg{Type: "auth_fail", Reason: "invalid token"})
		log.Printf("[인증 실패] %s (닉네임: %q)", c.conn.RemoteAddr(), m.Nick)
		// 실패 메시지가 전송될 시간을 잠깐 준다.
		time.Sleep(200 * time.Millisecond)
		return
	}
	c.nick = m.Nick
	if c.nick == "" {
		c.nick = "이름없음"
	}
	c.touch()
	c.hub.register(c)
	c.sendMsg(Msg{
		Type:         "auth_ok",
		SessionID:    c.id,
		UDPPort:      c.hub.udpPort,
		HeartbeatSec: heartbeatIntervalSec,
		MaxTalkSec:   int(c.hub.maxTalk.Seconds()),
	})

	// 2) 본 루프.
	for {
		c.conn.SetReadDeadline(time.Now().Add(readTimeout))
		_, data, err := c.conn.ReadMessage()
		if err != nil {
			return
		}
		var m Msg
		if err := json.Unmarshal(data, &m); err != nil {
			continue
		}
		c.touch()
		switch m.Type {
		case "ping":
			c.sendMsg(Msg{Type: "pong", TS: m.TS})
		case "ptt_start":
			c.hub.grantPTT(c)
		case "ptt_stop":
			c.hub.releasePTT(c, "정상 해제")
		}
	}
}
