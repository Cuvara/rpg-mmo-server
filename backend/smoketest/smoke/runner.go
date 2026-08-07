package smoke

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// Runner executes the smoke flow step by step, carrying state between steps.
type Runner struct {
	cfg Config
	out io.Writer
	hc  *http.Client

	sessionToken string // Nakama session token (step b)
	deviceID     string // device id authenticated with (step b)
	userID       string // Nakama user id (step c)
	username     string // Nakama username (nakama_account)
	gatewayJWT   string // gateway realtime token (step c)
	serverAddr   string // game server addr from EnterWorldResponse (step e)
	serverTrans  string // game server transport from EnterWorldResponse (step e)
	joinToken    string // join token from EnterWorldResponse (step e)

	// runStart is the wall clock at Run(); every persisted row this run asserts
	// on must be newer than it, which is what stops a stale row from passing.
	runStart     time.Time
	disconnectAt time.Time // when the gameplay socket was closed (reload hold math)
	persistedX   float32   // position read back out of player_states
	persistedY   float32

	results []StepResult
}

// NewRunner builds a Runner for cfg, writing progress to out.
func NewRunner(cfg Config, out io.Writer) *Runner {
	return &Runner{cfg: cfg, out: out, hc: &http.Client{Timeout: cfg.Timeout}}
}

// skip is returned by a step that could not run for want of configuration. The
// runner turns it into a SKIP line, or into a failure under --require-db.
type skip struct{ reason string }

func (s skip) Error() string { return s.reason }

// Run executes every step in order, stopping at the first failure, prints the
// summary and returns overall pass/fail.
func (r *Runner) Run() bool {
	r.runStart = time.Now()

	steps := []struct {
		name string
		fn   func() (string, error)
	}{
		{"nakama_health", r.stepNakamaHealth},
		{"device_auth", r.stepDeviceAuth},
		{"gateway_token_rpc", r.stepGatewayToken},
		{"gateway_auth", r.stepGatewayAuthEnter},  // MsgAuth + MsgEnterWorld share one conn
		{"gameserver_join", r.stepGameServerFlow}, // MsgJoinToken + inputs + snapshots + disconnect

		// --- persistence: the flow above proves the wire, not the databases ---
		//
		// The two Nakama checks go over the public HTTP API with the session
		// token already in hand, so they cost a few milliseconds and always run.
		// The three game-state checks need GAME_DB_URL and are skipped (loudly)
		// without it.
		{"nakama_account", r.guardDB(r.stepNakamaAccount, false)},
		{"nakama_profile", r.guardDB(r.stepNakamaProfile, false)},
		{"gamestate_migrations", r.guardDB(r.stepGameStateMigrations, true)},
		{"gamestate_player_row", r.guardDB(r.stepGameStatePlayerRow, true)},
		{"gamestate_reload", r.guardDB(r.stepGameStateReload, true)},
	}
	for _, s := range steps {
		start := time.Now()
		detail, err := s.fn()
		res := StepResult{Name: s.name, Latency: time.Since(start), Err: err, Detail: detail}
		var sk skip
		if errors.As(err, &sk) {
			// --require-db keeps the skip as a hard failure; otherwise report
			// SKIP and carry on with the remaining steps.
			if !r.cfg.RequireDB {
				res.Err, res.Skipped, res.Detail = nil, true, sk.reason
			}
		}
		r.results = append(r.results, res)
		if res.Err != nil {
			break
		}
	}
	return WriteSummary(r.out, r.results)
}

// guardDB wraps a persistence step with the reasons it may legitimately not
// run. needsDSN marks steps that talk to the game-state database directly.
func (r *Runner) guardDB(fn func() (string, error), needsDSN bool) func() (string, error) {
	return func() (string, error) {
		if r.cfg.SkipDB {
			return "", skip{"--skip-db / SMOKE_SKIP_DB set"}
		}
		if needsDSN && r.cfg.GameDBURL == "" {
			return "", skip{"GAME_DB_URL is unset — game-state persistence NOT verified " +
				"(set it, or pass --require-db to make this a failure)"}
		}
		return fn()
	}
}

// ---------------------------------------------------------------- step a

func (r *Runner) stepNakamaHealth() (string, error) {
	url := strings.TrimRight(r.cfg.NakamaURL, "/") + "/healthcheck"
	resp, err := r.hc.Get(url)
	if err != nil {
		return "", fmt.Errorf("GET %s: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("GET %s: status %d", url, resp.StatusCode)
	}
	return url, nil
}

// ---------------------------------------------------------------- step b

func (r *Runner) stepDeviceAuth() (string, error) {
	deviceID := r.cfg.DeviceID
	if deviceID == "" {
		suffix := make([]byte, 8)
		if _, err := rand.Read(suffix); err != nil {
			return "", fmt.Errorf("random device id: %w", err)
		}
		deviceID = "smoketest-" + hex.EncodeToString(suffix)
	}
	r.deviceID = deviceID

	body, _ := json.Marshal(map[string]string{"id": deviceID})
	url := strings.TrimRight(r.cfg.NakamaURL, "/") + "/v2/account/authenticate/device?create=true"
	req, err := http.NewRequest(http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return "", err
	}
	req.SetBasicAuth(r.cfg.ServerKey, "")
	req.Header.Set("Content-Type", "application/json")

	resp, err := r.hc.Do(req)
	if err != nil {
		return "", fmt.Errorf("POST %s: %w", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("device auth: status %d: %s", resp.StatusCode, truncate(raw, 200))
	}
	var out struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(raw, &out); err != nil {
		return "", fmt.Errorf("device auth: decode response: %w", err)
	}
	if out.Token == "" {
		return "", fmt.Errorf("device auth: empty session token")
	}
	r.sessionToken = out.Token
	return "device_id=" + deviceID, nil
}

// ---------------------------------------------------------------- step c

// gatewayTokenResponse mirrors nakama/auth.GatewayTokenResponse.
type gatewayTokenResponse struct {
	Token     string `json:"token"`
	UserID    string `json:"user_id"`
	ExpiresIn int    `json:"expires_in"`
}

func (r *Runner) stepGatewayToken() (string, error) {
	// The RPC payload is a JSON-encoded *string* containing the request JSON.
	url := strings.TrimRight(r.cfg.NakamaURL, "/") + "/v2/rpc/gateway_token"
	req, err := http.NewRequest(http.MethodPost, url, strings.NewReader(`"{}"`))
	if err != nil {
		return "", err
	}
	req.Header.Set("Authorization", "Bearer "+r.sessionToken)
	req.Header.Set("Content-Type", "application/json")

	resp, err := r.hc.Do(req)
	if err != nil {
		return "", fmt.Errorf("POST %s: %w", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("gateway_token rpc: status %d: %s", resp.StatusCode, truncate(raw, 200))
	}

	var envelope struct {
		Payload string `json:"payload"`
	}
	if err := json.Unmarshal(raw, &envelope); err != nil {
		return "", fmt.Errorf("gateway_token rpc: decode envelope: %w", err)
	}
	var tok gatewayTokenResponse
	if err := json.Unmarshal([]byte(envelope.Payload), &tok); err != nil {
		return "", fmt.Errorf("gateway_token rpc: decode payload: %w", err)
	}
	if tok.Token == "" || tok.UserID == "" {
		return "", fmt.Errorf("gateway_token rpc: missing token/user_id in payload")
	}

	// Verify the JWT locally with the shared secret — same check the gateway does.
	claims, err := jwt.Verify(tok.Token, r.cfg.JWTSecret)
	if err != nil {
		return "", fmt.Errorf("local jwt verify: %w", err)
	}
	if claims.UserID != tok.UserID {
		return "", fmt.Errorf("jwt sub %q != rpc user_id %q", claims.UserID, tok.UserID)
	}

	r.gatewayJWT = tok.Token
	r.userID = tok.UserID
	return "user_id=" + tok.UserID, nil
}

// ---------------------------------------------------------------- steps d+e

func (r *Runner) stepGatewayAuthEnter() (string, error) {
	conn, err := r.dial(r.cfg.Transport, r.cfg.GatewayAddr)
	if err != nil {
		return "", err
	}
	defer conn.Close()

	// MsgAuth — expect MsgAuthResp{OK}.
	var authResp messages.AuthResponse
	if err := r.roundTrip(conn, messages.MsgAuth, messages.AuthRequest{Token: r.gatewayJWT},
		messages.MsgAuthResp, &authResp); err != nil {
		return "", fmt.Errorf("gateway auth: %w", err)
	}
	if !authResp.OK {
		return "", fmt.Errorf("gateway auth rejected: %s", authResp.Error)
	}
	if authResp.UserID != r.userID {
		return "", fmt.Errorf("gateway auth user %q != %q", authResp.UserID, r.userID)
	}

	// MsgEnterWorld — expect ServerAddr + JoinToken.
	var enterResp messages.EnterWorldResponse
	if err := r.roundTrip(conn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: r.cfg.MapID},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		return "", fmt.Errorf("enter world: %w", err)
	}
	if enterResp.Error != "" {
		return "", fmt.Errorf("enter world rejected: %s", enterResp.Error)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		return "", fmt.Errorf("enter world: missing server_addr/join_token")
	}
	r.serverAddr = enterResp.ServerAddr
	r.joinToken = enterResp.JoinToken
	// The gateway tells us which transport the target game server speaks; an
	// omitted field means TCP (servers registered before the field existed).
	r.serverTrans = transport.Normalize(enterResp.Transport)
	return "transport=" + transport.Normalize(r.cfg.Transport) +
		" map=" + r.cfg.MapID +
		" server=" + enterResp.ServerAddr + " (" + r.serverTrans + ")", nil
}

// ---------------------------------------------------------------- steps f+g+h

func (r *Runner) stepGameServerFlow() (string, error) {
	conn, err := r.dial(r.serverTrans, r.serverAddr)
	if err != nil {
		return "", err
	}
	defer conn.Close()

	// MsgJoinToken must be the first frame on the socket.
	var joinResp messages.JoinTokenResponse
	if err := r.roundTrip(conn, messages.MsgJoinToken, messages.JoinTokenRequest{Token: r.joinToken},
		messages.MsgJoinTokenResp, &joinResp); err != nil {
		return "", fmt.Errorf("join: %w", err)
	}
	if !joinResp.OK {
		return "", fmt.Errorf("join rejected: %s", joinResp.Error)
	}
	if joinResp.UserID != r.userID {
		return "", fmt.Errorf("join user %q != %q", joinResp.UserID, r.userID)
	}

	// Reader goroutine: collect snapshots until the socket closes/errors.
	type snap struct {
		msg messages.SnapshotMessage
	}
	snapCh := make(chan snap, 256)
	readDone := make(chan struct{})
	go func() {
		defer close(readDone)
		for {
			env, err := r.recv(conn)
			if err != nil {
				return
			}
			if env.Type != messages.MsgSnapshot {
				continue
			}
			var s messages.SnapshotMessage
			if err := env.UnmarshalPayload(&s); err != nil {
				continue
			}
			select {
			case snapCh <- snap{s}:
			default: // never block the reader
			}
		}
	}()

	// Send inputs: fresh players spawn at (0,0). MoveX=1 is a unit DIRECTION, not a
	// displacement — the game server integrates direction*speed*dt once per tick, so
	// after N accepted inputs the entity sits at X ≈ N*speed/tickRate (with the default
	// 5 u/s at 15Hz: N/3). The exact value depends on server config, so the assertion
	// below only checks "moved forward, and not by a per-message teleport".
	for i := 0; i < r.cfg.Inputs; i++ {
		env, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
			Tick:  uint64(i + 1),
			MoveX: 1.0,
			MoveY: 0.0,
		})
		if err != nil {
			return "", fmt.Errorf("encode input: %w", err)
		}
		if err := r.send(conn, env); err != nil {
			return "", fmt.Errorf("send input %d: %w", i+1, err)
		}
		time.Sleep(r.cfg.InputInterval)
	}

	// Drain snapshots for a little longer so the final position lands.
	//
	// Snapshots are delta-encoded: only the first one (and every Nth after it) is a
	// full keyframe, the rest carry just what changed. A client that reads a single
	// snapshot in isolation would see an empty entity list, so merge the stream into
	// reconstructed world state exactly as the Unity client must.
	deadline := time.After(2 * time.Second)
	var (
		snapshots int
		lastX     float32
		seen      bool
		state     = messages.NewSnapshotState()
	)
drain:
	for {
		select {
		case s := <-snapCh:
			snapshots++
			// Smoketest speaks JSON, which never interns, so a desync here would
			// mean the server sent handles to a JSON connection — a bug worth
			// failing on rather than skipping past.
			if err := state.Apply(s.msg); err != nil {
				return "", fmt.Errorf("apply snapshot: %w", err)
			}
			if e, ok := state.Get(r.userID); ok {
				lastX = e.X
				seen = true
			}
			// Stop once every buffered snapshot is consumed, so lastX is the newest
			// authoritative position rather than an early one from the send phase.
			if seen && snapshots >= r.cfg.MinSnapshots && lastX > 0 && len(snapCh) == 0 {
				break drain
			}
		case <-deadline:
			break drain
		case <-readDone:
			break drain
		}
	}

	if snapshots < r.cfg.MinSnapshots {
		return "", fmt.Errorf("got %d snapshots, want >= %d", snapshots, r.cfg.MinSnapshots)
	}
	if !seen {
		return "", fmt.Errorf("player %s never appeared in a snapshot", r.userID)
	}
	// Movement is server-integrated: the player must have advanced along +X, but by
	// far less than one world unit per input. An X >= Inputs would mean the server is
	// back to treating move_x as a raw displacement (per-message teleport regression).
	if lastX <= 0 {
		return "", fmt.Errorf("position did not move as expected: X=%.2f, want > 0", lastX)
	}
	if maxX := float32(r.cfg.Inputs); lastX >= maxX {
		return "", fmt.Errorf("position moved too far: X=%.2f, want < %.2f (move_x must be a direction, not a displacement)", lastX, maxX)
	}

	// Clean disconnect: polite MsgDisconnect, then close (deferred). This
	// matters on KCP — UDP has no FIN, so without it the server only notices
	// the client is gone when the reconnect hold expires. KCP flushes on its
	// 10ms update tick and Close() does not drain, hence the short pause.
	if env, err := messages.NewEnvelope(messages.MsgDisconnect, struct{}{}); err == nil {
		_ = r.send(conn, env)
		time.Sleep(100 * time.Millisecond)
	}
	// Anchor for the reconnect-hold wait in the reload check: the entity is
	// evicted HoldTtl after the socket goes away, not after the run started.
	r.disconnectAt = time.Now()
	// ack_tick / keyframe counts are reported, not asserted: a server predating the
	// delta protocol sends neither, and the smoke test must stay green against it.
	return fmt.Sprintf("snapshots=%d (keyframes=%d deltas=%d) final_x=%.2f ack_tick=%d",
		snapshots, state.Keyframes, state.Deltas, lastX, state.AckTick), nil
}

// ---------------------------------------------------------------- wire utils

// dial connects over the given transport kind (empty means tcp), rewriting
// listen-style addresses into dialable loopback ones.
func (r *Runner) dial(kind, addr string) (net.Conn, error) {
	target := NormalizeDialAddr(addr)
	conn, err := transport.Dial(kind, target, r.cfg.Timeout)
	if err != nil {
		return nil, fmt.Errorf("dial %s over %s: %w", target, transport.Normalize(kind), err)
	}
	return conn, nil
}

func (r *Runner) send(conn net.Conn, env messages.Envelope) error {
	data, err := messages.Encode(env)
	if err != nil {
		return err
	}
	if err := conn.SetWriteDeadline(time.Now().Add(r.cfg.Timeout)); err != nil {
		return err
	}
	_, err = conn.Write(data)
	return err
}

func (r *Runner) recv(conn net.Conn) (messages.Envelope, error) {
	if err := conn.SetReadDeadline(time.Now().Add(r.cfg.Timeout)); err != nil {
		return messages.Envelope{}, err
	}
	return messages.Decode(conn)
}

// roundTrip sends one request envelope and waits for a response of wantType,
// decoding its payload into out. Unexpected frame types (e.g. an early
// snapshot) are skipped.
func (r *Runner) roundTrip(conn net.Conn, reqType messages.MsgType, reqPayload any,
	wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelope(reqType, reqPayload)
	if err != nil {
		return fmt.Errorf("encode: %w", err)
	}
	if err := r.send(conn, env); err != nil {
		return fmt.Errorf("send: %w", err)
	}
	for i := 0; i < 16; i++ { // bounded skip of interleaved frames
		resp, err := r.recv(conn)
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

func truncate(b []byte, n int) string {
	s := string(b)
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}
