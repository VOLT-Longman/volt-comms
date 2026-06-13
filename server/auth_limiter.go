package main

import (
	"net"
	"sync"
	"time"
)

// 인증 실패 제한 정책: 2분 동안 5회 실패하면 해당 host를 5분간 차단한다.
const (
	authFailWindow    = 2 * time.Minute // 실패 횟수를 집계하는 시간 창
	authFailThreshold = 5               // 차단을 발동시키는 실패 횟수
	authBlockDuration = 5 * time.Minute // 차단 유지 시간
)

// authRecord 는 한 host(IP)의 인증 실패 상태를 담는다.
type authRecord struct {
	failures     []time.Time // 시간 창 안에서 누적된 실패 시각
	blockedUntil time.Time   // 이 시각 이전에는 차단 상태
}

// authLimiter 는 IP 단위로 WebSocket 인증 실패를 추적하고 과도한 실패를 차단한다.
// 동시 접속 환경을 고려해 모든 상태 접근은 mu 로 보호한다.
type authLimiter struct {
	mu      sync.Mutex
	records map[string]*authRecord
}

func newAuthLimiter() *authLimiter {
	return &authLimiter{records: make(map[string]*authRecord)}
}

// hostOf 는 "ip:port" 형태의 원격 주소에서 host(IP)만 뽑아낸다.
// 포트 분리에 실패하면 원본을 그대로 키로 쓴다.
func hostOf(remoteAddr string) string {
	host, _, err := net.SplitHostPort(remoteAddr)
	if err != nil {
		return remoteAddr
	}
	return host
}

// allow 는 해당 주소가 지금 인증을 시도해도 되는지 반환한다.
// 차단 시간이 지난 기록은 초기화한다.
func (a *authLimiter) allow(remoteAddr string) bool {
	host := hostOf(remoteAddr)
	now := time.Now()
	a.mu.Lock()
	defer a.mu.Unlock()
	rec := a.records[host]
	if rec == nil {
		return true
	}
	if now.Before(rec.blockedUntil) {
		return false
	}
	if !rec.blockedUntil.IsZero() {
		// 차단이 만료됐으므로 기록을 비워 새 출발선을 준다.
		delete(a.records, host)
	}
	return true
}

// recordFailure 는 인증 실패 1건을 누적하고, 임계치 도달 시 차단을 건다.
func (a *authLimiter) recordFailure(remoteAddr string) {
	host := hostOf(remoteAddr)
	now := time.Now()
	a.mu.Lock()
	defer a.mu.Unlock()
	rec := a.records[host]
	if rec == nil {
		rec = &authRecord{}
		a.records[host] = rec
	}
	// 시간 창을 벗어난 오래된 실패는 무효화한다.
	rec.failures = pruneBefore(rec.failures, now.Add(-authFailWindow))
	rec.failures = append(rec.failures, now)
	if len(rec.failures) >= authFailThreshold {
		rec.blockedUntil = now.Add(authBlockDuration)
		rec.failures = nil // 차단됐으니 누적은 비운다.
	}
}

// recordSuccess 는 인증 성공 시 해당 host의 실패 기록을 모두 제거한다.
func (a *authLimiter) recordSuccess(remoteAddr string) {
	host := hostOf(remoteAddr)
	a.mu.Lock()
	delete(a.records, host)
	a.mu.Unlock()
}

// pruneBefore 는 cutoff 이전(이하)의 시각을 걸러낸 슬라이스를 돌려준다.
func pruneBefore(times []time.Time, cutoff time.Time) []time.Time {
	kept := times[:0]
	for _, t := range times {
		if t.After(cutoff) {
			kept = append(kept, t)
		}
	}
	return kept
}
