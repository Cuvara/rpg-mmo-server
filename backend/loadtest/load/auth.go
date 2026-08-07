package load

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
)

// authProvider drives the real Nakama login path used by -auth=nakama: device
// authenticate -> gateway_token RPC. It mirrors smoketest's steps b and c.
//
// This path is OFF by default on purpose: it makes every virtual player pay for
// a Nakama account creation and two HTTP round-trips through Nakama's Postgres,
// which would dominate the ramp and contaminate a measurement that is supposed
// to describe the game server's tick loop.
type authProvider struct {
	cfg Config
	hc  *http.Client
}

func newAuthProvider(cfg Config) *authProvider {
	return &authProvider{
		cfg: cfg,
		hc: &http.Client{
			Timeout: cfg.Timeout,
			Transport: &http.Transport{
				MaxIdleConns:        512,
				MaxIdleConnsPerHost: 512,
			},
		},
	}
}

func (a *authProvider) nakamaToken(ctx context.Context) (userID, token string, err error) {
	session, err := a.deviceAuth(ctx)
	if err != nil {
		return "", "", err
	}
	return a.gatewayToken(ctx, session)
}

func (a *authProvider) deviceAuth(ctx context.Context) (string, error) {
	suffix := make([]byte, 8)
	if _, err := rand.Read(suffix); err != nil {
		return "", fmt.Errorf("random device id: %w", err)
	}
	body, _ := json.Marshal(map[string]string{"id": "loadtest-" + hex.EncodeToString(suffix)})

	url := strings.TrimRight(a.cfg.NakamaURL, "/") + "/v2/account/authenticate/device?create=true"
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return "", err
	}
	req.SetBasicAuth(a.cfg.ServerKey, "")
	req.Header.Set("Content-Type", "application/json")

	raw, err := a.do(req, "device auth")
	if err != nil {
		return "", err
	}
	var out struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(raw, &out); err != nil {
		return "", fmt.Errorf("device auth: decode: %w", err)
	}
	if out.Token == "" {
		return "", fmt.Errorf("device auth: empty session token")
	}
	return out.Token, nil
}

func (a *authProvider) gatewayToken(ctx context.Context, session string) (string, string, error) {
	// The RPC payload is a JSON-encoded *string* containing the request JSON.
	url := strings.TrimRight(a.cfg.NakamaURL, "/") + "/v2/rpc/gateway_token"
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, strings.NewReader(`"{}"`))
	if err != nil {
		return "", "", err
	}
	req.Header.Set("Authorization", "Bearer "+session)
	req.Header.Set("Content-Type", "application/json")

	raw, err := a.do(req, "gateway_token rpc")
	if err != nil {
		return "", "", err
	}
	var envelope struct {
		Payload string `json:"payload"`
	}
	if err := json.Unmarshal(raw, &envelope); err != nil {
		return "", "", fmt.Errorf("gateway_token rpc: decode envelope: %w", err)
	}
	var tok struct {
		Token  string `json:"token"`
		UserID string `json:"user_id"`
	}
	if err := json.Unmarshal([]byte(envelope.Payload), &tok); err != nil {
		return "", "", fmt.Errorf("gateway_token rpc: decode payload: %w", err)
	}
	if tok.Token == "" || tok.UserID == "" {
		return "", "", fmt.Errorf("gateway_token rpc: missing token/user_id")
	}
	return tok.UserID, tok.Token, nil
}

func (a *authProvider) do(req *http.Request, what string) ([]byte, error) {
	resp, err := a.hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", what, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("%s: status %d: %s", what, resp.StatusCode, truncate(raw, 200))
	}
	return raw, nil
}

func truncate(b []byte, n int) string {
	s := string(b)
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}
