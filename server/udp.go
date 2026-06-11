package main

import (
	"encoding/binary"
	"log"
	"net"
	"sync"
)

// UDPRelay 는 음성 패킷을 수신해 발언권 보유자의 것만 다른 모든
// 등록된 클라이언트에게 중계한다.
type UDPRelay struct {
	conn *net.UDPConn
	hub  *Hub

	mu      sync.Mutex
	addrOf  map[uint32]*net.UDPAddr // session_id → 클라이언트 UDP 주소
	idOf    map[string]uint32       // 주소 문자열 → session_id (출처 검증용)
	relayed uint64                  // 중계한 패킷 수 (통계 로그용)
	dropped uint64                  // 발언권 없는 출처라 버린 패킷 수
}

func NewUDPRelay(port int, hub *Hub) (*UDPRelay, error) {
	conn, err := net.ListenUDP("udp", &net.UDPAddr{Port: port})
	if err != nil {
		return nil, err
	}
	return &UDPRelay{
		conn:   conn,
		hub:    hub,
		addrOf: make(map[uint32]*net.UDPAddr),
		idOf:   make(map[string]uint32),
	}, nil
}

// Remove 는 세션 종료 시 UDP 매핑을 정리한다.
func (r *UDPRelay) Remove(sessionID uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if addr, ok := r.addrOf[sessionID]; ok {
		delete(r.idOf, addr.String())
		delete(r.addrOf, sessionID)
	}
}

func (r *UDPRelay) Run() {
	buf := make([]byte, 2048)
	for {
		n, addr, err := r.conn.ReadFromUDP(buf)
		if err != nil {
			log.Printf("UDP 수신 오류: %v", err)
			continue
		}
		if n < 6 || buf[0] != udpMagic {
			continue
		}
		sessionID := binary.BigEndian.Uint32(buf[2:6])

		switch buf[1] {
		case udpTypeHandshake:
			r.handleHandshake(sessionID, addr)
		case udpTypeVoiceUp:
			if n < udpHeaderLen {
				continue
			}
			r.handleVoice(sessionID, addr, buf[:n])
		}
	}
}

func (r *UDPRelay) handleHandshake(sessionID uint32, addr *net.UDPAddr) {
	// WebSocket으로 인증된 세션만 등록한다.
	r.hub.mu.Lock()
	c, ok := r.hub.clients[sessionID]
	r.hub.mu.Unlock()
	if !ok {
		return
	}

	r.mu.Lock()
	prev, had := r.addrOf[sessionID]
	if had && prev.String() != addr.String() {
		delete(r.idOf, prev.String())
	}
	r.addrOf[sessionID] = addr
	r.idOf[addr.String()] = sessionID
	r.mu.Unlock()

	if !had {
		log.Printf("[UDP] 세션 %d (%s) 음성 경로 등록: %s", sessionID, c.nick, addr)
	}
	c.sendMsg(Msg{Type: "udp_ok"})
}

func (r *UDPRelay) handleVoice(sessionID uint32, addr *net.UDPAddr, pkt []byte) {
	// 출처 검증: 등록된 주소와 session_id가 일치해야 한다.
	r.mu.Lock()
	regID, ok := r.idOf[addr.String()]
	r.mu.Unlock()
	if !ok || regID != sessionID {
		return
	}

	// 발언권 보유자만 중계한다 (반이중 강제).
	holderID, ok := r.hub.holderID()
	if !ok || holderID != sessionID {
		r.mu.Lock()
		r.dropped++
		r.mu.Unlock()
		return
	}

	// 수신 패킷을 그대로 재사용: 종류 바이트만 S→C로 바꾼다.
	pkt[1] = udpTypeVoiceDown

	r.mu.Lock()
	targets := make([]*net.UDPAddr, 0, len(r.addrOf))
	for id, a := range r.addrOf {
		if id != sessionID {
			targets = append(targets, a)
		}
	}
	r.relayed++
	r.mu.Unlock()

	for _, a := range targets {
		r.conn.WriteToUDP(pkt, a)
	}
}
