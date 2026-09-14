//go:build integration

package integration

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/sealed"
)

// Two-hop byte tap.
//
// WHY THIS EXISTS. "The gameplay hop is encrypted" and "nothing sensitive is
// readable on the wire" are different claims, and only the second one matters to
// a credential. The sealed-session tests prove the first: they assert that each
// frame arrived with the sealed marker. They cannot prove the second, because a
// credential that leaks on the OTHER hop is invisible to a test that only reads
// one socket.
//
// So this test is a passive observer sitting in the path of BOTH hops at once,
// recording every byte in both directions, and then asking one question of the
// recording: which bearer credentials can be read out of it in the clear?
//
// It is written as an instrument, not as a guard — it reports what it found and
// asserts only the things that must be true for the recording to be meaningful
// (that the session really did complete, and that the gameplay hop really was
// sealed). See TestHopConfidentiality_Tap for the assertions that ARE guards.

// tapDir is where captures are written so a human can look at the bytes rather
// than at this test's summary of them.
func tapDir(t *testing.T) string {
	t.Helper()
	dir := filepath.Join(os.TempDir(), "rpg-hop-tap")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("mkdir capture dir: %v", err)
	}
	return dir
}

// tapSegment is one recorded direction of one connection.
type tapSegment struct {
	Dir   string // "c2s" or "s2c"
	Bytes []byte
}

// byteTap is a transparent TCP relay that records everything crossing it.
//
// It is deliberately a relay rather than a pcap: it needs no privileges, it
// works identically on WSL and CI, and — the point — it observes exactly what a
// passive attacker on the path observes, which is the octet stream and nothing
// else. It has no access to any key and does not decrypt anything.
type byteTap struct {
	label    string
	upstream string
	ln       net.Listener

	mu   sync.Mutex
	segs []*tapSegment
}

func startByteTap(t *testing.T, label, upstream string) *byteTap {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("tap %s listen: %v", label, err)
	}
	tp := &byteTap{label: label, upstream: upstream, ln: ln}
	go tp.acceptLoop()
	t.Cleanup(func() { _ = ln.Close() })
	return tp
}

func (tp *byteTap) addr() string { return tp.ln.Addr().String() }

func (tp *byteTap) acceptLoop() {
	for {
		down, err := tp.ln.Accept()
		if err != nil {
			return
		}
		go tp.relay(down)
	}
}

func (tp *byteTap) record(dir string) *tapSegment {
	seg := &tapSegment{Dir: dir}
	tp.mu.Lock()
	tp.segs = append(tp.segs, seg)
	tp.mu.Unlock()
	return seg
}

func (tp *byteTap) relay(down net.Conn) {
	defer down.Close()
	up, err := net.DialTimeout("tcp", tp.upstream, 5*time.Second)
	if err != nil {
		return
	}
	defer up.Close()

	c2s := tp.record("c2s")
	s2c := tp.record("s2c")

	var wg sync.WaitGroup
	wg.Add(2)
	copyRec := func(dst io.Writer, src io.Reader, seg *tapSegment) {
		defer wg.Done()
		buf := make([]byte, 32*1024)
		for {
			n, rerr := src.Read(buf)
			if n > 0 {
				tp.mu.Lock()
				seg.Bytes = append(seg.Bytes, buf[:n]...)
				tp.mu.Unlock()
				if _, werr := dst.Write(buf[:n]); werr != nil {
					return
				}
			}
			if rerr != nil {
				return
			}
		}
	}
	go copyRec(up, down, c2s)
	go copyRec(down, up, s2c)
	wg.Wait()
}

// snapshot returns a copy of everything recorded so far, plus the concatenation
// of every direction (which is what the credential scan runs over).
func (tp *byteTap) snapshot() (all []byte, perDir map[string][]byte) {
	tp.mu.Lock()
	defer tp.mu.Unlock()
	perDir = map[string][]byte{}
	for _, s := range tp.segs {
		all = append(all, s.Bytes...)
		perDir[s.Dir] = append(perDir[s.Dir], s.Bytes...)
	}
	return all, perDir
}

func (tp *byteTap) writeCapture(t *testing.T, dir string) string {
	t.Helper()
	all, per := tp.snapshot()
	path := filepath.Join(dir, tp.label+".bin")
	if err := os.WriteFile(path, all, 0o644); err != nil {
		t.Fatalf("write capture: %v", err)
	}
	for d, b := range per {
		p := filepath.Join(dir, tp.label+"."+d+".bin")
		if err := os.WriteFile(p, b, 0o644); err != nil {
			t.Fatalf("write capture %s: %v", d, err)
		}
	}
	return path
}

// ---------------------------------------------------------------- credentials

// jwtRe finds a compact-serialisation JWS anywhere in a byte stream. It works
// against both wire encodings for the same reason a passive attacker's would:
// a JWT is base64url ASCII whether it sits in a JSON string or a protobuf
// length-delimited field, and neither encoding transforms it.
var jwtRe = regexp.MustCompile(`eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}`)

// foundToken is one credential read out of a capture, with the claims it carries.
type foundToken struct {
	Raw      string
	Sub      string
	Jti      string
	ServerID string
	IssuedAt int64
	Expires  int64
}

func (f foundToken) lifetime() time.Duration {
	if f.IssuedAt == 0 || f.Expires == 0 {
		return 0
	}
	return time.Duration(f.Expires-f.IssuedAt) * time.Second
}

// scanTokens decodes every JWT visible in the clear in a capture. Nothing here
// uses a key: this is exactly what an observer with no secrets can do.
func scanTokens(capture []byte) []foundToken {
	var out []foundToken
	seen := map[string]bool{}
	for _, m := range jwtRe.FindAll(capture, -1) {
		raw := string(m)
		if seen[raw] {
			continue
		}
		seen[raw] = true
		parts := strings.Split(raw, ".")
		if len(parts) != 3 {
			continue
		}
		payload, err := base64.RawURLEncoding.DecodeString(parts[1])
		if err != nil {
			continue
		}
		var claims struct {
			Sub      string `json:"sub"`
			Jti      string `json:"jti"`
			ServerID string `json:"sid"`
			IssuedAt int64  `json:"iat"`
			Expires  int64  `json:"exp"`
		}
		if err := json.Unmarshal(payload, &claims); err != nil {
			continue
		}
		out = append(out, foundToken{
			Raw:      raw,
			Sub:      claims.Sub,
			Jti:      claims.Jti,
			ServerID: claims.ServerID,
			IssuedAt: claims.IssuedAt,
			Expires:  claims.Expires,
		})
	}
	return out
}

func shortToken(raw string) string {
	if len(raw) <= 24 {
		return raw
	}
	return raw[:12] + "..." + raw[len(raw)-8:]
}

// countSealedFrames walks the [4-byte BE length][body] framing and counts the
// bodies whose first byte is the sealed marker. A body that is not sealed is
// reported too — a handshake that completes and then falls back to cleartext is
// precisely the failure this distinguishes.
func countSealedFrames(capture []byte) (sealedN, plainN int) {
	for i := 0; i+4 <= len(capture); {
		n := int(uint32(capture[i])<<24 | uint32(capture[i+1])<<16 | uint32(capture[i+2])<<8 | uint32(capture[i+3]))
		i += 4
		if n < 0 || i+n > len(capture) {
			return sealedN, plainN
		}
		if n > 0 && capture[i] == sealed.Marker {
			sealedN++
		} else {
			plainN++
		}
		i += n
	}
	return sealedN, plainN
}

// ------------------------------------------------------------------- the test

// TestHopConfidentiality_Tap reproduces the 2026-09-10 two-hop measurement
// recorded in backend/docs/ROADMAP-SECURITY.md.
//
// It runs a full sealed session with a passive byte tap on BOTH hops, then reads
// the bearer credentials out of each capture without using any key.
//
// The auth token is signed with constants.SessionTTL — the same lifetime the
// Nakama gateway_token RPC issues (auth/config.go: TokenTTL: constants.SessionTTL)
// — so the lifetime this test reports is the shipped one and not a test artefact.
func TestHopConfidentiality_Tap(t *testing.T) {
	requireDotnet(t)

	dir := tapDir(t)

	// --- hop 2 (gameplay): tap in front of the C# game server -----------------
	// The server is spawned with NO --sealed argument, so this runs the DEFAULT
	// (require). If that default is ever moved back to off, the sealed-frame
	// assertion below fails rather than the test quietly measuring a plaintext
	// session and reporting it as sealed.
	gsAddr, gsCleanup := startDotnetGameServerSealedDefault(t, nil, nil)
	t.Cleanup(gsCleanup)
	gsTap := startByteTap(t, "gameplay-hop", gsAddr)

	// --- hop 1 (gateway): the gateway is told to hand out the TAP's address ---
	// so the client's second connection walks through the gameplay tap.
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsTap.addr())
	t.Cleanup(gwCleanup)
	gwTap := startByteTap(t, "gateway-hop", gwAddr)

	// --- the client, through both taps ---------------------------------------
	gw, err := NewMockClient(gwTap.addr())
	if err != nil {
		t.Fatalf("connect to gateway tap: %v", err)
	}
	defer gw.Close()

	const userID = "tap-player"
	authToken, err := jwt.Sign(userID, dotnetJWTSecret, constants.SessionTTL)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	authEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgAuth,
		messages.AuthRequest{Token: authToken})
	if err := gw.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	authRespEnv, err := gw.Receive()
	if err != nil {
		t.Fatalf("receive auth response: %v", err)
	}
	var authResp messages.AuthResponse
	if err := authRespEnv.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("auth failed: %s", authResp.Error)
	}

	enterEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgEnterWorld,
		messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := gw.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := gw.Receive()
	if err != nil {
		t.Fatalf("receive enter world response: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
		t.Fatalf("unmarshal enter world response: %v", err)
	}
	if enterResp.ServerAddr != gsTap.addr() {
		t.Fatalf("gateway handed out %q, expected the gameplay tap at %q — the second hop would not be observed",
			enterResp.ServerAddr, gsTap.addr())
	}

	gs, err := dialSealedTestClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to game server through tap: %v", err)
	}
	t.Cleanup(gs.Close)

	joinEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gs.send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}
	joinRespEnv, _, err := gs.recv(5 * time.Second)
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}

	res, err := gs.handshake(enterResp.JoinToken, dotnetJoinTokenSecret)
	if err != nil {
		t.Fatalf("sealed handshake: %v", err)
	}

	// Drive real gameplay traffic so the capture contains sealed frames rather
	// than only a handshake.
	if err := gs.send(mustProto(t, messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1})); err != nil {
		t.Fatalf("send sealed input: %v", err)
	}
	var snapshots int
	for i := 0; i < 32 && snapshots < 4; i++ {
		env, wasSealed, err := gs.recv(5 * time.Second)
		if err != nil {
			t.Fatalf("receive after handshake: %v", err)
		}
		if !wasSealed {
			t.Fatalf("frame of type %d arrived UNSEALED after a sealed handshake", env.Type)
		}
		if env.Type == messages.MsgSnapshot {
			snapshots++
		}
	}
	if snapshots == 0 {
		t.Fatal("no snapshot arrived; the capture would not represent a live session")
	}

	// Give the relay a moment to record the last frames it forwarded.
	time.Sleep(200 * time.Millisecond)

	// --- read the recordings --------------------------------------------------
	gwPath := gwTap.writeCapture(t, dir)
	gsPath := gsTap.writeCapture(t, dir)

	gwAll, _ := gwTap.snapshot()
	gsAll, gsPer := gsTap.snapshot()

	gwTokens := scanTokens(gwAll)
	gsTokens := scanTokens(gsAll)

	sealedS2C, plainS2C := countSealedFrames(gsPer["s2c"])
	sealedC2S, plainC2S := countSealedFrames(gsPer["c2s"])

	t.Logf("captures: %s (%d bytes), %s (%d bytes)", gwPath, len(gwAll), gsPath, len(gsAll))
	t.Logf("gameplay hop framing: c2s sealed=%d plain=%d, s2c sealed=%d plain=%d (binding_verified=%v)",
		sealedC2S, plainC2S, sealedS2C, plainS2C, res.BindingVerified)

	report := func(hop string, toks []foundToken) {
		if len(toks) == 0 {
			t.Logf("%s hop: NO bearer credential readable in the clear", hop)
			return
		}
		for _, tok := range toks {
			kind := "auth token"
			if tok.ServerID != "" || tok.Jti != "" {
				kind = "join token"
			}
			t.Logf("%s hop: READABLE %s sub=%q jti=%q sid=%q lifetime=%s token=%s",
				hop, kind, tok.Sub, tok.Jti, tok.ServerID, tok.lifetime(), shortToken(tok.Raw))
		}
	}
	report("gateway", gwTokens)
	report("gameplay", gsTokens)

	// --- the assertions that make this a measurement and not a demo -----------

	// 1. The gameplay hop really was sealed. Without this, everything below
	//    would be measuring a plaintext session and calling it sealed.
	if sealedS2C == 0 {
		t.Fatalf("gameplay hop carried no sealed frame (s2c sealed=%d plain=%d) — the session was not sealed",
			sealedS2C, plainS2C)
	}

	// 2. The auth token is readable on the gateway hop, with the shipped
	//    lifetime. This is the finding; when the gateway hop is sealed this
	//    assertion is the one that must be inverted.
	var authFound *foundToken
	for i := range gwTokens {
		if gwTokens[i].Raw == authToken {
			authFound = &gwTokens[i]
		}
	}
	if authFound == nil {
		t.Fatalf("auth token was NOT readable on the gateway hop — either the tap missed it or the hop is already sealed; check %s", gwPath)
	}
	if got, want := authFound.lifetime(), constants.SessionTTL; got != want {
		t.Errorf("auth token lifetime read off the wire = %s, want %s (constants.SessionTTL)", got, want)
	}

	// 3. The SAME join token — same jti — crosses both hops in the clear. This
	//    is the claim that decides the ordering in ROADMAP-SECURITY.md §2: if it
	//    is false, sealing the gameplay hop's join exchange alone would be worth
	//    something.
	joinOnGateway := tokenWithRaw(gwTokens, enterResp.JoinToken)
	joinOnGameplay := tokenWithRaw(gsTokens, enterResp.JoinToken)
	if joinOnGateway == nil {
		t.Errorf("join token not readable on the gateway hop; check %s", gwPath)
	}
	if joinOnGameplay == nil {
		t.Errorf("join token not readable on the gameplay hop; check %s", gsPath)
	}
	if joinOnGateway != nil && joinOnGameplay != nil {
		if joinOnGateway.Jti != joinOnGameplay.Jti {
			t.Errorf("join token jti differs across hops: gateway=%q gameplay=%q", joinOnGateway.Jti, joinOnGameplay.Jti)
		}
		if got, want := joinOnGateway.lifetime(), constants.JoinTokenTTL; got != want {
			t.Errorf("join token lifetime read off the wire = %s, want %s (constants.JoinTokenTTL)", got, want)
		}
	}

	fmt.Fprintf(os.Stdout, "\n[tap] MEASURED: gateway hop auth token lifetime=%s, join token lifetime=%s, gameplay hop sealed frames s2c=%d\n",
		authFound.lifetime(), constants.JoinTokenTTL, sealedS2C)
}

// TestHopConfidentiality_CapturedCredentialReuse measures the "single-use"
// column of the table in ROADMAP-SECURITY.md, which the byte tap alone cannot:
// a tap shows a credential is READABLE, not what it is worth once read.
//
// So this test does what an observer who read the tap would do next: replay each
// captured credential from a NEW connection and see whether it is honoured.
func TestHopConfidentiality_CapturedCredentialReuse(t *testing.T) {
	requireDotnet(t)

	gsAddr, gsCleanup := startDotnetGameServerSealedDefault(t, nil, nil)
	t.Cleanup(gsCleanup)
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	t.Cleanup(gwCleanup)

	authToken, err := jwt.Sign("replay-player", dotnetJWTSecret, constants.SessionTTL)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	// --- the auth token, replayed on a fresh gateway connection ---------------
	// Twice, from two independent connections. If the second is accepted, the
	// credential is reusable and its value to an observer is its full lifetime.
	var joinToken string
	for attempt := 1; attempt <= 2; attempt++ {
		gw, err := NewMockClient(gwAddr)
		if err != nil {
			t.Fatalf("attempt %d: connect to gateway: %v", attempt, err)
		}
		authEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgAuth,
			messages.AuthRequest{Token: authToken})
		if err := gw.Send(authEnv); err != nil {
			t.Fatalf("attempt %d: send auth: %v", attempt, err)
		}
		respEnv, err := gw.Receive()
		if err != nil {
			t.Fatalf("attempt %d: receive auth response: %v", attempt, err)
		}
		var resp messages.AuthResponse
		if err := respEnv.UnmarshalPayload(&resp); err != nil {
			t.Fatalf("attempt %d: unmarshal auth response: %v", attempt, err)
		}
		if !resp.OK {
			t.Fatalf("attempt %d: auth token was REFUSED (%s) — it is single-use after all, and the table in ROADMAP-SECURITY.md is wrong",
				attempt, resp.Error)
		}
		t.Logf("auth token accepted on connection %d (reusable)", attempt)

		if attempt == 2 {
			enterEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgEnterWorld,
				messages.EnterWorldRequest{MapID: dotnetMapID})
			if err := gw.Send(enterEnv); err != nil {
				t.Fatalf("send enter world: %v", err)
			}
			enterRespEnv, err := gw.Receive()
			if err != nil {
				t.Fatalf("receive enter world response: %v", err)
			}
			var enterResp messages.EnterWorldResponse
			if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
				t.Fatalf("unmarshal enter world response: %v", err)
			}
			joinToken = enterResp.JoinToken
			gsAddr = enterResp.ServerAddr
		}
		gw.Close()
	}
	if joinToken == "" {
		t.Fatal("no join token issued")
	}

	// This is the finding stated as an executable claim: one captured auth token
	// mints a fresh join token on demand, for its whole hour. Sealing the
	// gameplay hop does not touch that.

	// --- the join token, replayed on a fresh game-server connection -----------
	joinOnce := func(attempt int) (bool, string) {
		gs, err := dialSealedTestClient(gsAddr)
		if err != nil {
			t.Fatalf("attempt %d: connect to game server: %v", attempt, err)
		}
		defer gs.Close()
		env, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgJoinToken,
			messages.JoinTokenRequest{Token: joinToken})
		if err := gs.send(env); err != nil {
			t.Fatalf("attempt %d: send join token: %v", attempt, err)
		}
		respEnv, _, err := gs.recv(5 * time.Second)
		if err != nil {
			t.Fatalf("attempt %d: receive join response: %v", attempt, err)
		}
		var resp messages.JoinTokenResponse
		if err := respEnv.UnmarshalPayload(&resp); err != nil {
			t.Fatalf("attempt %d: unmarshal join response: %v", attempt, err)
		}
		return resp.OK, resp.Error
	}

	if ok, errMsg := joinOnce(1); !ok {
		t.Fatalf("first use of the join token was refused: %s", errMsg)
	}
	t.Log("join token accepted on its first use")
	if ok, _ := joinOnce(2); ok {
		t.Error("join token was accepted TWICE — it is not single-use, and the table in ROADMAP-SECURITY.md is wrong")
	} else {
		t.Log("join token refused on replay (single-use, JtiTracker)")
	}
}

func tokenWithRaw(toks []foundToken, raw string) *foundToken {
	for i := range toks {
		if toks[i].Raw == raw {
			return &toks[i]
		}
	}
	return nil
}
