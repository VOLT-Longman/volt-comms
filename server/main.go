// VOLT 함대 지휘망 무전 서버.
//
// 제어 채널: WebSocket(JSON, 토큰 인증, 발언권 중재, 하트비트)
// 음성 채널: UDP (Opus 프레임 릴레이, 현재 발언권 보유자만 중계)
package main

import (
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"time"
)

var version = "dev"

func main() {
	listen := flag.String("listen", ":8443", "제어(WebSocket) 수신 주소")
	udpPort := flag.Int("udp-port", 4001, "음성(UDP) 포트")
	token := flag.String("token", "", "공유 인증 토큰 (또는 환경변수 VOLT_TOKEN)")
	tlsCert := flag.String("tls-cert", "", "TLS 인증서 파일 (지정 시 wss:// 활성화)")
	tlsKey := flag.String("tls-key", "", "TLS 개인키 파일")
	maxTalk := flag.Duration("max-talk", 30*time.Second, "연속 발언 최대 시간 (초과 시 발언권 강제 회수)")
	hbTimeout := flag.Duration("heartbeat-timeout", 12*time.Second, "발언권 보유자의 하트비트가 이 시간 이상 끊기면 강제 회수")
	showVersion := flag.Bool("version", false, "버전 출력")
	flag.Parse()

	if *showVersion {
		fmt.Println("volt-server", version)
		return
	}

	if *token == "" {
		*token = os.Getenv("VOLT_TOKEN")
	}
	if *token == "" {
		log.Fatal("인증 토큰이 필요합니다: -token 플래그 또는 VOLT_TOKEN 환경변수를 설정하세요")
	}

	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	hub := NewHub(*token, *udpPort, *maxTalk, *hbTimeout)

	relay, err := NewUDPRelay(*udpPort, hub)
	if err != nil {
		log.Fatalf("UDP 소켓 열기 실패: %v", err)
	}
	hub.relay = relay
	go relay.Run()
	go hub.Watchdog()

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", hub.ServeWS)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprintln(w, "ok")
	})

	srv := &http.Server{
		Addr:              *listen,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
	}

	if *tlsCert != "" && *tlsKey != "" {
		log.Printf("volt-server %s 시작: 제어 wss://%s/ws, 음성 UDP %d", version, *listen, *udpPort)
		err = srv.ListenAndServeTLS(*tlsCert, *tlsKey)
	} else {
		log.Printf("volt-server %s 시작: 제어 ws://%s/ws (TLS 비활성), 음성 UDP %d", version, *listen, *udpPort)
		err = srv.ListenAndServe()
	}
	log.Fatal(err)
}
