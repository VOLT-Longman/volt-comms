// VOLT 함대 지휘망 무전 서버.
//
// 제어 채널: WebSocket(JSON, 토큰 인증, 발언권 중재, 하트비트)
// 음성 채널: UDP (Opus 프레임 릴레이, 현재 발언권 보유자만 중계)
package main

import (
	"bufio"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"strings"
	"time"
)

var version = "dev"

// interactive 는 콘솔에서 직접(더블클릭 등) 실행됐는지 추정한다.
// 윈도우에서 비개발자가 더블클릭하면 토큰 입력을 받고, 종료 시 창이
// 바로 닫혀 오류를 못 보는 일이 없도록 잠깐 대기하기 위해 쓴다.
func interactive() bool {
	info, err := os.Stdin.Stat()
	if err != nil {
		return false
	}
	return (info.Mode() & os.ModeCharDevice) != 0
}

func promptToken() string {
	fmt.Print("공유 인증 토큰을 입력하고 Enter 를 누르세요 (함대원에게 알려줄 비밀번호): ")
	line, _ := bufio.NewReader(os.Stdin).ReadString('\n')
	return strings.TrimSpace(line)
}

// waitBeforeExit 는 콘솔 직접 실행 시 창이 즉시 닫히지 않게 Enter 를 기다린다.
func waitBeforeExit() {
	if !interactive() {
		return
	}
	fmt.Print("\n종료하려면 Enter 키를 누르세요...")
	bufio.NewReader(os.Stdin).ReadString('\n')
}

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
	if *token == "" && interactive() {
		// 인자 없이 더블클릭으로 실행한 경우: 토큰을 물어본다.
		*token = promptToken()
	}
	if *token == "" {
		fmt.Println("인증 토큰이 필요합니다: -token 플래그 또는 VOLT_TOKEN 환경변수를 설정하세요")
		waitBeforeExit()
		os.Exit(1)
	}

	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	hub := NewHub(*token, *udpPort, *maxTalk, *hbTimeout)

	relay, err := NewUDPRelay(*udpPort, hub)
	if err != nil {
		log.Printf("UDP 소켓 열기 실패: %v", err)
		log.Printf("→ %d 포트를 다른 프로그램이 쓰고 있거나 권한이 없을 수 있습니다.", *udpPort)
		waitBeforeExit()
		os.Exit(1)
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
		log.Printf("같은 PC 점검: 브라우저에서 http://localhost%s/healthz 열면 ok 가 보입니다.", localHealthAddr(*listen))
		err = srv.ListenAndServeTLS(*tlsCert, *tlsKey)
	} else {
		log.Printf("volt-server %s 시작: 제어 ws://%s/ws (TLS 비활성), 음성 UDP %d", version, *listen, *udpPort)
		log.Printf("같은 PC 점검: 브라우저에서 http://localhost%s/healthz 열면 ok 가 보입니다.", localHealthAddr(*listen))
		err = srv.ListenAndServe()
	}
	log.Printf("서버가 종료되었습니다: %v", err)
	waitBeforeExit()
	os.Exit(1)
}

// localHealthAddr 는 listen 주소에서 포트만 뽑아 "localhost:포트" 점검용 접미사를 만든다.
// (":8443" → ":8443", "0.0.0.0:8443" → ":8443")
func localHealthAddr(listen string) string {
	if i := strings.LastIndex(listen, ":"); i >= 0 {
		return listen[i:]
	}
	return ":" + listen
}
