package main

import (
	"testing"
	"time"
)

func TestAuthLimiterBlocksAfterRepeatedFailures(t *testing.T) {
	a := newAuthLimiter()
	const addr = "192.0.2.10:5000"

	// 임계치 직전까지는 계속 허용돼야 한다.
	for i := 1; i < authFailThreshold; i++ {
		a.recordFailure(addr)
		if !a.allow(addr) {
			t.Fatalf("실패 %d회(임계치 %d 미만)에서 차단됨 — 허용돼야 한다", i, authFailThreshold)
		}
	}

	// 임계치 번째 실패로 차단돼야 한다.
	a.recordFailure(addr)
	if a.allow(addr) {
		t.Fatalf("실패 %d회 이후에도 허용됨 — 차단돼야 한다", authFailThreshold)
	}

	// 다른 host는 영향을 받지 않아야 한다.
	if !a.allow("203.0.113.7:5000") {
		t.Fatal("무관한 host가 차단됨")
	}
}

func TestAuthLimiterSuccessClearsFailures(t *testing.T) {
	a := newAuthLimiter()
	const addr = "198.51.100.5:6000"

	// 임계치 직전까지 실패를 쌓는다.
	for i := 1; i < authFailThreshold; i++ {
		a.recordFailure(addr)
	}

	// 인증 성공 처리로 실패 기록이 초기화돼야 한다.
	a.recordSuccess(addr)
	if !a.allow(addr) {
		t.Fatal("성공 처리 후에도 차단 상태")
	}

	// 초기화됐으므로 다시 임계치 직전까지 실패해도 차단되지 않아야 한다.
	for i := 1; i < authFailThreshold; i++ {
		a.recordFailure(addr)
		if !a.allow(addr) {
			t.Fatalf("성공 후 재실패 %d회(임계치 미만)에서 조기 차단됨", i)
		}
	}
}

func TestAuthLimiterPrunesStaleRecords(t *testing.T) {
	a := newAuthLimiter()
	clock := time.Now()
	a.now = func() time.Time { return clock }
	a.lastSweep = clock

	// 서로 다른 host가 한 번씩만 실패한다 (저속 분산 실패 시나리오).
	for _, h := range []string{"203.0.113.1:1", "203.0.113.2:1", "203.0.113.3:1"} {
		a.recordFailure(h)
	}
	if got := len(a.records); got != 3 {
		t.Fatalf("실패 직후 record 수 = %d, want 3", got)
	}

	// 시간 창과 스윕 간격을 넘긴 뒤 새 실패가 들어오면 stale entry가 정리돼야 한다.
	clock = clock.Add(authBlockDuration + time.Minute)
	a.recordFailure("198.51.100.9:1")
	if got := len(a.records); got != 1 {
		t.Fatalf("스윕 후 record 수 = %d, want 1 (새 host만 남아야 함)", got)
	}
}
