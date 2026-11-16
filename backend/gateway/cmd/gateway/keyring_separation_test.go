package main

import (
	"context"
	"errors"
	"flag"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"

	sharedjwt "github.com/duycuong/rpg-mmo/shared/jwt"
)

// These tests pin MG-02 of backend/docs/MULTI-GAME-CROSS-REVIEW.md: a key that
// verifies client auth tokens (JWT_SECRET) must never also sit in a game's
// join keyring, and two games must never share a join key. Before the fix the
// gateway compared the settings as whole strings and only warned, so
// JWT_SECRET=auth-new,auth-old next to JOIN_TOKEN_SECRET_EMBER=join-ember,auth-old
// started cleanly and handed ember's game-server pods a key that mints auth
// tokens for any user and any game.

// secretsUsedInTests lists every key value in the tables below. No error
// message may contain any of them: the check names settings and games only.
var secretsUsedInTests = []string{
	"auth-new", "auth-old", "join-cuvara", "join-ember", "join-arena",
	"shared-join", "join-only",
}

func TestKeyringKeys_MatchesParseKeyring(t *testing.T) {
	specs := []string{
		"",
		",",
		",,",
		"   ",
		" , ,",
		"a",
		" a ",
		"a,b",
		" a , b ,",
		",a,,b,",
		"a,a",
		"\ta\t,\nb\n",
		"new,old",
	}
	for _, spec := range specs {
		t.Run(strings.ReplaceAll(spec, "\n", `\n`), func(t *testing.T) {
			keys := keyringKeys(spec)
			kr, err := sharedjwt.ParseKeyring(spec)
			if len(keys) == 0 {
				if err == nil {
					t.Fatalf("keyringKeys(%q) is empty but ParseKeyring accepted it with %d keys", spec, kr.Len())
				}
				return
			}
			if err != nil {
				t.Fatalf("keyringKeys(%q) = %d keys but ParseKeyring refused it: %v", spec, len(keys), err)
			}
			if kr.Len() != len(keys) {
				t.Fatalf("keyringKeys(%q) has %d keys, ParseKeyring has %d", spec, len(keys), kr.Len())
			}
			if kr.Signing() != keys[0] {
				t.Fatalf("keyringKeys(%q)[0] = %q, ParseKeyring signs with %q", spec, keys[0], kr.Signing())
			}
			for _, k := range keys {
				if k == "" || k != strings.TrimSpace(k) {
					t.Fatalf("keyringKeys(%q) returned an untrimmed or empty key %q", spec, k)
				}
			}
		})
	}
}

func TestCheckKeyringSeparation(t *testing.T) {
	cuvara := func(spec string) gameKeyring {
		return gameKeyring{Game: "cuvara", Setting: "JOIN_TOKEN_SECRET", Spec: spec}
	}
	ember := func(spec string) gameKeyring {
		return gameKeyring{Game: "ember", Setting: "JOIN_TOKEN_SECRET_EMBER", Spec: spec}
	}
	arena := func(spec string) gameKeyring {
		return gameKeyring{Game: "arena", Setting: "JOIN_TOKEN_SECRET_ARENA", Spec: spec}
	}

	tests := []struct {
		name  string
		auth  string
		joins []gameKeyring
		// want lists substrings the error must contain; nil means no error.
		want []string
		// notWant lists substrings the error must not contain, on top of
		// every value in secretsUsedInTests.
		notWant []string
	}{
		{
			name:  "review configuration: ember join rotation key is the old auth key",
			auth:  "auth-new,auth-old",
			joins: []gameKeyring{cuvara("join-cuvara"), ember("join-ember,auth-old")},
			want:  []string{"ember", "JOIN_TOKEN_SECRET_EMBER", "JWT_SECRET"},
			// The default game is clean; it must not be blamed.
			notWant: []string{`"cuvara"`},
		},
		{
			name:  "whitespace around the shared key still matches",
			auth:  "auth-new, auth-old ",
			joins: []gameKeyring{cuvara("join-cuvara"), ember("join-ember , auth-old")},
			want:  []string{"ember", "JOIN_TOKEN_SECRET_EMBER", "JWT_SECRET"},
		},
		{
			name:  "shared key is the signing key of the join ring",
			auth:  " auth-old ",
			joins: []gameKeyring{cuvara("join-cuvara"), ember("auth-old,join-ember")},
			want:  []string{"ember", "JWT_SECRET"},
		},
		{
			name:  "default join keyring shares a key with auth",
			auth:  "auth-new,auth-old",
			joins: []gameKeyring{cuvara("join-cuvara,auth-new"), ember("join-ember")},
			want:  []string{"cuvara", "JOIN_TOKEN_SECRET", "JWT_SECRET"},
			// ember is clean; it must not be blamed.
			notWant: []string{"ember", "EMBER"},
		},
		{
			name:  "default join keyring equal to auth",
			auth:  "auth-new",
			joins: []gameKeyring{cuvara("auth-new")},
			want:  []string{"cuvara", "JOIN_TOKEN_SECRET", "JWT_SECRET"},
		},
		{
			name:  "default game and ember share a join key",
			auth:  "auth-new",
			joins: []gameKeyring{cuvara("join-cuvara,shared-join"), ember("shared-join")},
			want:  []string{"cuvara", "ember", "JOIN_TOKEN_SECRET", "JOIN_TOKEN_SECRET_EMBER"},
			// auth is clean; it must not be blamed.
			notWant: []string{"JWT_SECRET"},
		},
		{
			name:    "ember and a third game share a join key",
			auth:    "auth-new",
			joins:   []gameKeyring{cuvara("join-cuvara"), ember("join-ember, shared-join"), arena(" shared-join ,join-arena")},
			want:    []string{"ember", "arena", "JOIN_TOKEN_SECRET_EMBER", "JOIN_TOKEN_SECRET_ARENA"},
			notWant: []string{"JWT_SECRET", `"cuvara"`},
		},
		{
			name:  "several violations are all reported",
			auth:  "auth-new,auth-old",
			joins: []gameKeyring{cuvara("join-cuvara,auth-new"), ember("join-ember,auth-old"), arena("join-ember")},
			want:  []string{"cuvara", "ember", "arena", "JWT_SECRET"},
		},
		{
			name:  "distinct keyrings pass",
			auth:  "auth-new,auth-old",
			joins: []gameKeyring{cuvara("join-cuvara"), ember("join-ember"), arena("join-arena")},
		},
		{
			name:  "distinct keyrings with rotation lists and whitespace pass",
			auth:  " auth-new , auth-old ,",
			joins: []gameKeyring{cuvara("join-cuvara, join-only"), ember(",join-ember,")},
		},
		{
			name:  "a duplicate inside one keyring is not a separation error",
			auth:  "auth-new,auth-new",
			joins: []gameKeyring{cuvara("join-cuvara,join-cuvara"), ember("join-ember, join-ember")},
		},
		{
			name:  "default game only, distinct",
			auth:  "auth-new",
			joins: []gameKeyring{cuvara("join-cuvara")},
		},
		{
			name:  "a substring is not a shared key",
			auth:  "auth-old",
			joins: []gameKeyring{cuvara("auth-old-join")},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := checkKeyringSeparation(tt.auth, tt.joins)
			if tt.want == nil {
				if err != nil {
					t.Fatalf("checkKeyringSeparation = %v, want nil for distinct keyrings", err)
				}
				return
			}
			if err == nil {
				t.Fatalf("checkKeyringSeparation = nil, want an error naming %v", tt.want)
			}
			msg := err.Error()
			for _, w := range tt.want {
				if !strings.Contains(msg, w) {
					t.Errorf("error %q does not name %q", msg, w)
				}
			}
			for _, nw := range tt.notWant {
				if strings.Contains(msg, nw) {
					t.Errorf("error %q names %q, which is not part of any violation", msg, nw)
				}
			}
			for _, secret := range secretsUsedInTests {
				if strings.Contains(msg, secret) {
					t.Errorf("error %q leaks the key value %q", msg, secret)
				}
			}
			if !errors.Is(err, errKeyringOverlap) {
				t.Errorf("error %q does not wrap errKeyringOverlap", msg)
			}
		})
	}
}

// TestCheckKeyringSeparation_Deterministic proves the same input always yields
// the same message, in the caller's game order: operators diff these lines
// across restarts.
func TestCheckKeyringSeparation_Deterministic(t *testing.T) {
	joins := []gameKeyring{
		{Game: "cuvara", Setting: "JOIN_TOKEN_SECRET", Spec: "join-cuvara,auth-new"},
		{Game: "ember", Setting: "JOIN_TOKEN_SECRET_EMBER", Spec: "join-ember,auth-old,shared-join"},
		{Game: "arena", Setting: "JOIN_TOKEN_SECRET_ARENA", Spec: "shared-join"},
	}
	first := checkKeyringSeparation("auth-new,auth-old", joins)
	if first == nil {
		t.Fatal("checkKeyringSeparation = nil, want an error")
	}
	for i := 0; i < 20; i++ {
		if got := checkKeyringSeparation("auth-new,auth-old", joins); got == nil || got.Error() != first.Error() {
			t.Fatalf("run %d: %v, want %v", i, got, first)
		}
	}
	msg := first.Error()
	iCuvara, iEmber, iArena := strings.Index(msg, `"cuvara"`), strings.Index(msg, `"ember"`), strings.Index(msg, `"arena"`)
	if iCuvara < 0 || iEmber < 0 || iArena < 0 || !(iCuvara < iEmber && iEmber < iArena) {
		t.Fatalf("error %q does not report the games in configured order cuvara, ember, arena", msg)
	}
}

// --- Process-level proof ----------------------------------------------------
//
// The pure check is only half of MG-02: the gateway binary must actually run it
// and exit before serving. The test below re-executes this test binary as the
// gateway (main()) with the review's environment and asserts the process dies
// with the separation error. Before the fix the child got past the old WARN and
// blocked in gw.Run on an ephemeral port until the parent's deadline killed it.

const keyringChildEnv = "GATEWAY_KEYRING_SEPARATION_CHILD"

// TestKeyringSeparationChild is not a test on its own: it is the child process
// body for TestGateway_*Keyring*. Without the marker it returns immediately.
func TestKeyringSeparationChild(t *testing.T) {
	if os.Getenv(keyringChildEnv) != "1" {
		t.Skip("child process body; runs only under the keyring separation process tests")
	}
	os.Args = []string{"gateway"}
	flag.CommandLine = flag.NewFlagSet("gateway", flag.ExitOnError)
	main()
	// main only returns if the gateway listener stopped cleanly; nothing in
	// these tests does that, so a return is itself a failure the parent sees.
	os.Exit(3)
}

// gatewayChildEnv is the parent's environment minus everything the gateway
// reads, plus vars. Keeping the rest (PATH, SYSTEMROOT, TEMP) matters on
// Windows, where a process without SYSTEMROOT cannot open a socket.
func gatewayChildEnv(vars map[string]string) []string {
	drop := []string{
		"GAMES", "JWT_SECRET", "JOIN_TOKEN_SECRET", "REDIS_ADDR", "REDIS_PASSWORD",
		"GATEWAY_", "METRICS_ADDR", "ALLOCATOR", "ALLOCATION_", "NAKAMA_",
		"TRANSPORT_KEY", "LOG_LEVEL", keyringChildEnv,
	}
	var env []string
	for _, kv := range os.Environ() {
		name := strings.ToUpper(strings.SplitN(kv, "=", 2)[0])
		keep := true
		for _, d := range drop {
			if strings.HasPrefix(name, d) {
				keep = false
				break
			}
		}
		if keep {
			env = append(env, kv)
		}
	}
	env = append(env, keyringChildEnv+"=1")
	for k, v := range vars {
		env = append(env, k+"="+v)
	}
	return env
}

// runGatewayChild runs main() in a child process and returns its exit code and
// combined output. A child still running at the deadline is killed and the
// test fails: the gateway reached its listener, which these cases forbid.
func runGatewayChild(t *testing.T, vars map[string]string) (int, string) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, os.Args[0], "-test.run=^TestKeyringSeparationChild$", "-test.count=1")
	cmd.Env = gatewayChildEnv(vars)
	out, err := cmd.CombinedOutput()
	if ctx.Err() != nil {
		t.Fatalf("gateway child was still running after 20s and was killed: it passed startup checks and reached its listener.\noutput:\n%s", out)
	}
	var exitErr *exec.ExitError
	switch {
	case err == nil:
		return 0, string(out)
	case errors.As(err, &exitErr):
		return exitErr.ExitCode(), string(out)
	default:
		t.Fatalf("run gateway child: %v", err)
		return 0, ""
	}
}

// TestGateway_RefusesReviewKeyringConfiguration is MG-02's exact scenario.
func TestGateway_RefusesReviewKeyringConfiguration(t *testing.T) {
	code, out := runGatewayChild(t, map[string]string{
		"GAMES":                   "cuvara,ember",
		"JWT_SECRET":              "auth-new,auth-old",
		"JOIN_TOKEN_SECRET":       "join-cuvara",
		"JOIN_TOKEN_SECRET_EMBER": "join-ember,auth-old",
		// Nothing may bind a fixed port even if the check were missing.
		"GATEWAY_ADDR":    "127.0.0.1:0",
		"METRICS_ADDR":    "off",
		"GATEWAY_BACKEND": "memory",
	})
	if code == 0 {
		t.Fatalf("gateway exited 0, want a non-zero refusal.\noutput:\n%s", out)
	}
	for _, w := range []string{"ember", "JOIN_TOKEN_SECRET_EMBER", "JWT_SECRET", "refusing to start"} {
		if !strings.Contains(out, w) {
			t.Errorf("gateway output does not contain %q.\noutput:\n%s", w, out)
		}
	}
	for _, secret := range []string{"auth-old", "auth-new", "join-ember", "join-cuvara"} {
		if strings.Contains(out, secret) {
			t.Errorf("gateway output leaks the key value %q.\noutput:\n%s", secret, out)
		}
	}
	if strings.Contains(out, "starting gateway") {
		t.Errorf("gateway reached \"starting gateway\" before refusing.\noutput:\n%s", out)
	}
}

// TestGateway_RefusesGamesSharingJoinKey covers join-vs-join overlap at the
// process level.
func TestGateway_RefusesGamesSharingJoinKey(t *testing.T) {
	code, out := runGatewayChild(t, map[string]string{
		"GAMES":                   "cuvara,ember,arena",
		"JWT_SECRET":              "auth-new",
		"JOIN_TOKEN_SECRET":       "join-cuvara",
		"JOIN_TOKEN_SECRET_EMBER": "join-ember,shared-join",
		"JOIN_TOKEN_SECRET_ARENA": "shared-join",
		"GATEWAY_ADDR":            "127.0.0.1:0",
		"METRICS_ADDR":            "off",
		"GATEWAY_BACKEND":         "memory",
	})
	if code == 0 {
		t.Fatalf("gateway exited 0, want a non-zero refusal.\noutput:\n%s", out)
	}
	for _, w := range []string{"ember", "arena", "refusing to start"} {
		if !strings.Contains(out, w) {
			t.Errorf("gateway output does not contain %q.\noutput:\n%s", w, out)
		}
	}
	if strings.Contains(out, "shared-join") {
		t.Errorf("gateway output leaks the shared key value.\noutput:\n%s", out)
	}
}

// TestGateway_DistinctKeyringsPassTheCheck proves distinct keyrings start as
// before, without leaving a gateway running: an unknown backend is the first
// fatal check AFTER key separation, so a child that dies on "invalid backend"
// has passed the separation check. METRICS_ADDR=off keeps the metrics
// listener, which starts between the two, from binding anything.
func TestGateway_DistinctKeyringsPassTheCheck(t *testing.T) {
	code, out := runGatewayChild(t, map[string]string{
		"GAMES":                   "cuvara,ember",
		"JWT_SECRET":              "auth-new,auth-old",
		"JOIN_TOKEN_SECRET":       "join-cuvara",
		"JOIN_TOKEN_SECRET_EMBER": "join-ember",
		"GATEWAY_ADDR":            "127.0.0.1:0",
		"METRICS_ADDR":            "off",
		"GATEWAY_BACKEND":         "not-a-backend",
	})
	if code == 0 {
		t.Fatalf("gateway exited 0, want the deliberate invalid-backend exit.\noutput:\n%s", out)
	}
	if !strings.Contains(out, "invalid backend") {
		t.Fatalf("gateway did not reach the backend check, so distinct keyrings were refused earlier.\noutput:\n%s", out)
	}
	if strings.Contains(out, "share a key") || strings.Contains(out, "keyring separation") {
		t.Fatalf("distinct keyrings were reported as overlapping.\noutput:\n%s", out)
	}
}
