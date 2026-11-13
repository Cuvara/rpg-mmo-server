//go:build localstack

package auth_test

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/nakama/auth"
	"github.com/duycuong/rpg-mmo/shared/gameid"
	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// Run only after the owner approves the local-stack gate, against real Nakama
// configured with --runtime.env GAMES=cuvara,<MULTIGAME_TEST_GAME>.
func TestSessionRefresh_GameVarLocalStack(t *testing.T) {
	game := os.Getenv("MULTIGAME_TEST_GAME")
	if game == "" {
		t.Fatal("MULTIGAME_TEST_GAME is required; Nakama must run with --runtime.env GAMES=cuvara,<game>")
	}
	if !gameid.ValidGameID(game) || gameid.IsDefaultGame(game) {
		t.Fatal("MULTIGAME_TEST_GAME must be a valid non-default game; Nakama must run with --runtime.env GAMES=cuvara,<game>")
	}
	baseURL := os.Getenv("NAKAMA_URL")
	if baseURL == "" {
		baseURL = "http://127.0.0.1:7350"
	}
	serverKey := os.Getenv("NAKAMA_SERVER_KEY")
	if serverKey == "" {
		serverKey = "defaultkey"
	}
	baseURL = strings.TrimRight(baseURL, "/")
	client := &http.Client{Timeout: 15 * time.Second}
	defer client.CloseIdleConnections()

	// Requests use Basic auth for authenticate/refresh and Bearer auth for RPCs.
	post := func(path, bearer string, body any) (int, []byte) {
		t.Helper()
		encoded, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("encode %s request: %v", path, err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, baseURL+path, bytes.NewReader(encoded))
		if err != nil {
			t.Fatalf("create %s request: %v", path, err)
		}
		req.Header.Set("Content-Type", "application/json")
		if bearer == "" {
			req.SetBasicAuth(serverKey, "")
		} else {
			req.Header.Set("Authorization", "Bearer "+bearer)
		}
		res, err := client.Do(req)
		if err != nil {
			t.Fatalf("POST %s: %v", path, err)
		}
		defer res.Body.Close()
		data, err := io.ReadAll(io.LimitReader(res.Body, 1<<20))
		if err != nil {
			t.Fatalf("read %s response: %v", path, err)
		}
		return res.StatusCode, data
	}
	assertOK := func(status int, body []byte) {
		t.Helper()
		if status < 200 || status >= 300 {
			t.Fatalf("Nakama returned HTTP %d: %s", status, body)
		}
	}
	assertDenied := func(status int, body []byte) {
		t.Helper()
		if status >= 200 && status < 300 {
			t.Fatalf("Nakama accepted disallowed game (HTTP %d)", status)
		}
		if status >= 500 {
			t.Fatalf("expected a game rejection, got server failure HTTP %d: %s", status, body)
		}
	}
	randomID := func() string {
		t.Helper()
		var random [12]byte
		if _, err := rand.Read(random[:]); err != nil {
			t.Fatalf("random device ID: %v", err)
		}
		return "l4refresh" + hex.EncodeToString(random[:])
	}
	type session struct {
		Token        string `json:"token"`
		RefreshToken string `json:"refresh_token"`
	}
	decodeSession := func(body []byte) session {
		t.Helper()
		var result session
		if err := json.Unmarshal(body, &result); err != nil {
			t.Fatalf("decode session: %v", err)
		}
		if result.Token == "" || result.RefreshToken == "" {
			t.Fatal("Nakama session is missing a session token or refresh token")
		}
		return result
	}
	gatewayGame := func(token string) string {
		t.Helper()
		// The Nakama REST RPC accepts a JSON string containing the payload JSON.
		status, body := post("/v2/rpc/"+auth.RPCGatewayToken, token, "{}")
		assertOK(status, body)
		var envelope struct {
			Payload string `json:"payload"`
		}
		if err := json.Unmarshal(body, &envelope); err != nil {
			t.Fatalf("decode RPC response: %v", err)
		}
		var response auth.GatewayTokenResponse
		if err := json.Unmarshal([]byte(envelope.Payload), &response); err != nil {
			t.Fatalf("decode gateway_token payload: %v", err)
		}
		claims, err := jwt.ParseUnverified(response.Token)
		if err != nil {
			t.Fatalf("parse gateway token: %v", err)
		}
		return claims.GameID
	}

	status, body := post("/v2/account/authenticate/device?create=true", "", map[string]any{
		"id": randomID(), "vars": map[string]string{"game": game},
	})
	assertOK(status, body)
	initial := decodeSession(body)
	if got := gatewayGame(initial.Token); got != game {
		t.Fatalf("initial gateway gid = %q, want %q", got, game)
	}

	// Omit vars entirely: real Nakama must preserve the refresh token's vars.
	status, body = post("/v2/account/session/refresh", "", map[string]string{"token": initial.RefreshToken})
	assertOK(status, body)
	refreshed := decodeSession(body)
	parts := strings.Split(refreshed.Token, ".")
	if len(parts) != 3 {
		t.Fatal("refreshed session token is not a JWT")
	}
	claimsJSON, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatalf("decode refreshed session claims: %v", err)
	}
	var sessionClaims struct {
		Vars map[string]string `json:"vrs"`
	}
	if err := json.Unmarshal(claimsJSON, &sessionClaims); err != nil {
		t.Fatalf("decode refreshed session vars: %v", err)
	}
	if got := sessionClaims.Vars["game"]; got != game {
		t.Fatalf("refreshed session vrs.game = %q, want %q", got, game)
	}
	if got := gatewayGame(refreshed.Token); got != game {
		t.Fatalf("refreshed gateway gid = %q, want %q", got, game)
	}

	// A fresh, valid random ID is outside the configured cuvara,<game> list.
	var random [8]byte
	if _, err := rand.Read(random[:]); err != nil {
		t.Fatalf("random denied game: %v", err)
	}
	deniedGame := "denied" + hex.EncodeToString(random[:])
	status, body = post("/v2/account/authenticate/device?create=true", "", map[string]any{
		"id": randomID(), "vars": map[string]string{"game": deniedGame},
	})
	assertDenied(status, body)
	status, body = post("/v2/account/session/refresh", "", map[string]any{
		"token": refreshed.RefreshToken, "vars": map[string]string{"game": deniedGame},
	})
	assertDenied(status, body)
}
