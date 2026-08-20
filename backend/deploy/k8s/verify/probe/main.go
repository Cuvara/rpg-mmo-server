// Command probe performs the two protocol-level observations the verification
// suite cannot make with curl or redis-cli, and prints them as machine-readable
// key=value lines for verify.sh to classify.
//
//	probe token       -- Nakama device auth + gateway_token RPC + local JWT verify.
//	                     Proves the Nakama Go plugin is LOADED and signing with the
//	                     same secret the gateway verifies with. A process that is
//	                     merely up cannot pass this.
//	probe enterworld  -- the gateway hop only: MsgAuth then MsgEnterWorld for a map,
//	                     printing either the advertised address or the exact refusal
//	                     string. It never dials the game server, so it neither joins
//	                     a world nor writes player state.
//
// Exit code 0 means "the observation was made"; it does NOT mean the deployment
// is healthy. Only a transport/config failure exits non-zero. verify.sh decides
// pass/fail from the printed RESULT= line, so a refusal is data, not an error.
package main

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

type config struct {
	nakamaURL string
	serverKey string
	gateway   string
	transport string
	jwtSecret string
	mapID     string
	deviceID  string
	timeout   time.Duration
}

func main() {
	if len(os.Args) < 2 {
		fatal("usage: probe <token|enterworld> [flags]")
	}
	sub := os.Args[1]

	var cfg config
	fs := flag.NewFlagSet("probe "+sub, flag.ExitOnError)
	fs.StringVar(&cfg.nakamaURL, "nakama-url", envOr("NAKAMA_URL", "http://localhost:7350"), "Nakama HTTP base URL")
	fs.StringVar(&cfg.serverKey, "server-key", envOr("NAKAMA_SERVER_KEY", "defaultkey"), "Nakama server key")
	fs.StringVar(&cfg.gateway, "gateway-addr", envOr("GATEWAY_ADDR", "127.0.0.1:8000"), "Gateway address")
	fs.StringVar(&cfg.transport, "transport", envOr("TRANSPORT", "tcp"), "Gateway transport: tcp or kcp")
	fs.StringVar(&cfg.jwtSecret, "jwt-secret", os.Getenv("JWT_SECRET"), "Shared HS256 secret, for local token verification")
	fs.StringVar(&cfg.mapID, "map-id", envOr("PROBE_MAP_ID", "map_01"), "Map to request in MsgEnterWorld")
	fs.StringVar(&cfg.deviceID, "device-id", os.Getenv("PROBE_DEVICE_ID"), "Nakama device id (default: random per run)")
	fs.DurationVar(&cfg.timeout, "timeout", 10*time.Second, "Per-operation network timeout")
	_ = fs.Parse(os.Args[2:])

	if cfg.jwtSecret == "" {
		fatal("JWT_SECRET is required (env or --jwt-secret): without it a forged token would pass")
	}

	switch sub {
	case "token":
		tok, user := mustToken(cfg)
		_ = tok
		fmt.Printf("RESULT=ok user_id=%s\n", user)
	case "enterworld":
		tok, _ := mustToken(cfg)
		enterWorld(cfg, tok)
	default:
		fatal("unknown subcommand %q (want token|enterworld)", sub)
	}
}

// mustToken runs device auth + the gateway_token RPC and verifies the returned
// JWT locally with the shared secret — the same check the gateway performs.
func mustToken(cfg config) (token, userID string) {
	hc := &http.Client{Timeout: cfg.timeout}
	base := strings.TrimRight(cfg.nakamaURL, "/")

	deviceID := cfg.deviceID
	if deviceID == "" {
		b := make([]byte, 8)
		if _, err := rand.Read(b); err != nil {
			fatal("random device id: %v", err)
		}
		deviceID = "verify-" + hex.EncodeToString(b)
	}

	body, _ := json.Marshal(map[string]string{"id": deviceID})
	req, err := http.NewRequest(http.MethodPost, base+"/v2/account/authenticate/device?create=true", bytes.NewReader(body))
	if err != nil {
		fatal("device auth request: %v", err)
	}
	req.SetBasicAuth(cfg.serverKey, "")
	req.Header.Set("Content-Type", "application/json")
	raw, status, err := do(hc, req)
	if err != nil {
		fatal("device auth: %v", err)
	}
	if status != http.StatusOK {
		fatal("device auth: status %d: %s", status, truncate(raw, 200))
	}
	var authOut struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(raw, &authOut); err != nil || authOut.Token == "" {
		fatal("device auth: no session token in response: %s", truncate(raw, 200))
	}

	// The RPC payload is a JSON-encoded *string* containing the request JSON.
	rpcReq, err := http.NewRequest(http.MethodPost, base+"/v2/rpc/gateway_token", strings.NewReader(`"{}"`))
	if err != nil {
		fatal("gateway_token request: %v", err)
	}
	rpcReq.Header.Set("Authorization", "Bearer "+authOut.Token)
	rpcReq.Header.Set("Content-Type", "application/json")
	raw, status, err = do(hc, rpcReq)
	if err != nil {
		fatal("gateway_token rpc: %v", err)
	}
	if status != http.StatusOK {
		fatal("gateway_token rpc: status %d: %s (is the Nakama Go plugin loaded?)", status, truncate(raw, 200))
	}
	var env struct {
		Payload string `json:"payload"`
	}
	if err := json.Unmarshal(raw, &env); err != nil {
		fatal("gateway_token rpc: decode envelope: %v", err)
	}
	var tok struct {
		Token  string `json:"token"`
		UserID string `json:"user_id"`
	}
	if err := json.Unmarshal([]byte(env.Payload), &tok); err != nil {
		fatal("gateway_token rpc: decode payload: %v", err)
	}
	if tok.Token == "" || tok.UserID == "" {
		fatal("gateway_token rpc: missing token/user_id in payload")
	}
	claims, err := jwt.Verify(tok.Token, cfg.jwtSecret)
	if err != nil {
		fatal("local jwt verify: %v (Nakama and the gateway disagree on JWT_SECRET)", err)
	}
	if claims.UserID != tok.UserID {
		fatal("jwt sub %q != rpc user_id %q", claims.UserID, tok.UserID)
	}
	return tok.Token, tok.UserID
}

// enterWorld performs the gateway hop and prints what came back. A refusal is a
// successful observation: the caller decides whether that refusal was the right
// one.
func enterWorld(cfg config, token string) {
	conn, err := transport.Dial(cfg.transport, normalizeDial(cfg.gateway), cfg.timeout)
	if err != nil {
		fatal("dial gateway %s over %s: %v", cfg.gateway, cfg.transport, err)
	}
	defer conn.Close()

	var authResp messages.AuthResponse
	if err := roundTrip(conn, cfg.timeout, messages.MsgAuth, messages.AuthRequest{Token: token},
		messages.MsgAuthResp, &authResp); err != nil {
		fatal("gateway auth: %v", err)
	}
	if !authResp.OK {
		fatal("gateway auth rejected: %s", authResp.Error)
	}

	var enter messages.EnterWorldResponse
	if err := roundTrip(conn, cfg.timeout, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: cfg.mapID},
		messages.MsgEnterWorldResp, &enter); err != nil {
		fatal("enter world: %v", err)
	}
	if enter.Error != "" {
		fmt.Printf("RESULT=refused map=%s message=%q\n", cfg.mapID, enter.Error)
		return
	}
	fmt.Printf("RESULT=ok map=%s server_addr=%s transport=%s join_token_len=%d\n",
		cfg.mapID, enter.ServerAddr, transport.Normalize(enter.Transport), len(enter.JoinToken))
}

func roundTrip(conn interface {
	io.ReadWriter
	SetReadDeadline(time.Time) error
	SetWriteDeadline(time.Time) error
}, timeout time.Duration, reqType messages.MsgType, payload any, wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelope(reqType, payload)
	if err != nil {
		return fmt.Errorf("encode: %w", err)
	}
	data, err := messages.Encode(env)
	if err != nil {
		return fmt.Errorf("encode envelope: %w", err)
	}
	if err := conn.SetWriteDeadline(time.Now().Add(timeout)); err != nil {
		return err
	}
	if _, err := conn.Write(data); err != nil {
		return fmt.Errorf("send: %w", err)
	}
	for i := 0; i < 16; i++ { // bounded skip of interleaved frames
		if err := conn.SetReadDeadline(time.Now().Add(timeout)); err != nil {
			return err
		}
		resp, err := messages.Decode(conn)
		if err != nil {
			return fmt.Errorf("recv: %w", err)
		}
		if resp.Type != wantType {
			continue
		}
		return resp.UnmarshalPayload(out)
	}
	return fmt.Errorf("no frame of type %d received", wantType)
}

// normalizeDial turns a listen-style ":8000" into something dialable. This
// applies only to the operator-supplied gateway address, never to an address a
// server advertised — that distinction is the whole point of --strict-addr in
// the smoke test.
func normalizeDial(addr string) string {
	if strings.HasPrefix(addr, ":") {
		return "127.0.0.1" + addr
	}
	return addr
}

func do(hc *http.Client, req *http.Request) ([]byte, int, error) {
	resp, err := hc.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	return raw, resp.StatusCode, nil
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func truncate(b []byte, n int) string {
	s := string(b)
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "probe: "+format+"\n", args...)
	fmt.Println("RESULT=error")
	os.Exit(2)
}
