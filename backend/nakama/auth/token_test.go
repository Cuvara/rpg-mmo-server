package auth

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/heroiclabs/nakama-common/runtime"
)

func testConfig() Config {
	return Config{JWTSecret: "test-secret", TokenTTL: time.Hour, MinPasswordLength: 8}
}

func TestIssueGatewayToken(t *testing.T) {
	tests := []struct {
		name     string
		userID   string
		serverID string
		wantErr  bool
	}{
		{"user only", "user-123", "", false},
		{"user and server", "user-123", "map_01-abc", false},
		{"empty user rejected", "", "", true},
	}

	cfg := testConfig()
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			resp, err := IssueGatewayToken(tt.userID, tt.serverID, cfg)
			if tt.wantErr {
				if err == nil {
					t.Fatal("IssueGatewayToken() expected error, got nil")
				}
				return
			}
			if err != nil {
				t.Fatalf("IssueGatewayToken() error: %v", err)
			}
			if resp.UserID != tt.userID {
				t.Errorf("UserID = %q, want %q", resp.UserID, tt.userID)
			}
			if resp.ExpiresIn != int64(cfg.TokenTTL.Seconds()) {
				t.Errorf("ExpiresIn = %d, want %d", resp.ExpiresIn, int64(cfg.TokenTTL.Seconds()))
			}

			// The Gateway verifies with shared/jwt and the same secret.
			claims, err := jwt.Verify(resp.Token, cfg.JWTSecret)
			if err != nil {
				t.Fatalf("jwt.Verify() error: %v", err)
			}
			if claims.UserID != tt.userID {
				t.Errorf("claims.UserID = %q, want %q", claims.UserID, tt.userID)
			}
			if claims.ServerID != tt.serverID {
				t.Errorf("claims.ServerID = %q, want %q", claims.ServerID, tt.serverID)
			}
			if claims.ExpireAt <= claims.IssuedAt {
				t.Errorf("ExpireAt %d must be after IssuedAt %d", claims.ExpireAt, claims.IssuedAt)
			}
		})
	}
}

func TestIssueGatewayToken_WrongSecretRejected(t *testing.T) {
	resp, err := IssueGatewayToken("user-1", "", testConfig())
	if err != nil {
		t.Fatalf("IssueGatewayToken() error: %v", err)
	}
	if _, err := jwt.Verify(resp.Token, "other-secret"); err == nil {
		t.Error("jwt.Verify() should fail with a different secret")
	}
}

func TestGatewayTokenRPC(t *testing.T) {
	env := map[string]string{"JWT_SECRET": "rpc-secret"}

	tests := []struct {
		name    string
		userID  string
		payload string
		wantErr error
	}{
		{"ok empty payload", "user-9", "", nil},
		{"ok with server id", "user-9", `{"server_id":"gs-1"}`, nil},
		{"no session", "", "", ErrUnauthenticated},
		{"bad payload", "user-9", "{not-json", ErrInvalidPayload},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, env) //nolint:staticcheck // Nakama uses string context keys
			if tt.userID != "" {
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USER_ID, tt.userID) //nolint:staticcheck
			}

			out, err := GatewayTokenRPC(ctx, noopLogger{}, nil, nil, tt.payload)
			if tt.wantErr != nil {
				if err != tt.wantErr {
					t.Fatalf("GatewayTokenRPC() error = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("GatewayTokenRPC() error: %v", err)
			}

			var resp GatewayTokenResponse
			if err := json.Unmarshal([]byte(out), &resp); err != nil {
				t.Fatalf("unmarshal response: %v", err)
			}
			claims, err := jwt.Verify(resp.Token, env["JWT_SECRET"])
			if err != nil {
				t.Fatalf("jwt.Verify() with env secret error: %v", err)
			}
			if claims.UserID != tt.userID {
				t.Errorf("claims.UserID = %q, want %q", claims.UserID, tt.userID)
			}
			if resp.ExpiresIn != int64(constants.SessionTTL.Seconds()) {
				t.Errorf("ExpiresIn = %d, want %d", resp.ExpiresIn, int64(constants.SessionTTL.Seconds()))
			}
		})
	}
}

func TestLoadConfig_EnvOverride(t *testing.T) {
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, map[string]string{"JWT_SECRET": "from-env"}) //nolint:staticcheck
	cfg := LoadConfig(ctx)
	if cfg.JWTSecret != "from-env" {
		t.Errorf("JWTSecret = %q, want %q", cfg.JWTSecret, "from-env")
	}
	if cfg.TokenTTL != constants.SessionTTL {
		t.Errorf("TokenTTL = %v, want %v", cfg.TokenTTL, constants.SessionTTL)
	}
	if cfg.MinPasswordLength != DefaultMinPasswordLength {
		t.Errorf("MinPasswordLength = %d, want %d", cfg.MinPasswordLength, DefaultMinPasswordLength)
	}
}

// noopLogger implements runtime.Logger and discards everything.
type noopLogger struct{}

func (noopLogger) Debug(string, ...interface{})                       {}
func (noopLogger) Info(string, ...interface{})                        {}
func (noopLogger) Warn(string, ...interface{})                        {}
func (noopLogger) Error(string, ...interface{})                       {}
func (l noopLogger) WithField(string, interface{}) runtime.Logger     { return l }
func (l noopLogger) WithFields(map[string]interface{}) runtime.Logger { return l }
func (noopLogger) Fields() map[string]interface{}                     { return nil }
