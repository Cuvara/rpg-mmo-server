package jwt

import (
	"strings"
	"testing"
	"time"
)

func TestParseKeyring(t *testing.T) {
	tests := []struct {
		name    string
		spec    string
		want    []string
		wantErr bool
	}{
		{name: "single secret", spec: "s1", want: []string{"s1"}},
		{name: "rotation pair", spec: "new,old", want: []string{"new", "old"}},
		{name: "whitespace trimmed", spec: " new , old ", want: []string{"new", "old"}},
		{name: "trailing comma ignored", spec: "new,", want: []string{"new"}},
		{name: "empty entries dropped", spec: "new,,old", want: []string{"new", "old"}},
		{name: "empty spec is an error", spec: "", wantErr: true},
		{name: "only separators is an error", spec: ",,", wantErr: true},
		{name: "only whitespace is an error", spec: "  ", wantErr: true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			k, err := ParseKeyring(tt.spec)
			if tt.wantErr {
				if err == nil {
					t.Fatalf("ParseKeyring(%q) should fail", tt.spec)
				}
				if k.Valid() {
					t.Error("failed ParseKeyring must return an invalid keyring")
				}
				return
			}
			if err != nil {
				t.Fatalf("ParseKeyring(%q) error: %v", tt.spec, err)
			}
			if k.Len() != len(tt.want) {
				t.Fatalf("Len() = %d, want %d", k.Len(), len(tt.want))
			}
			if k.Signing() != tt.want[0] {
				t.Errorf("Signing() = %q, want %q (first entry signs)", k.Signing(), tt.want[0])
			}
		})
	}
}

// TestKeyringRotation is the behaviour the whole type exists for: after the
// operator deploys JWT_SECRET="new,old", tokens minted under either secret keep
// working, and everything newly minted uses "new".
func TestKeyringRotation(t *testing.T) {
	const oldSecret, newSecret = "old-secret", "new-secret"

	oldToken, err := Sign("player-1", oldSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign with old secret: %v", err)
	}

	rotating, err := ParseKeyring(newSecret + "," + oldSecret)
	if err != nil {
		t.Fatalf("ParseKeyring: %v", err)
	}

	// 1. A token signed with the previous secret still verifies — nobody is
	//    logged out by the deploy.
	claims, err := rotating.Verify(oldToken)
	if err != nil {
		t.Fatalf("old token must still verify during rotation: %v", err)
	}
	if claims.UserID != "player-1" {
		t.Errorf("UserID = %q, want %q", claims.UserID, "player-1")
	}

	// 2. Newly issued tokens use the current secret.
	fresh, err := rotating.Sign("player-2", time.Hour)
	if err != nil {
		t.Fatalf("sign with keyring: %v", err)
	}
	if _, err := Verify(fresh, newSecret); err != nil {
		t.Errorf("fresh token must verify under the current secret: %v", err)
	}
	if _, err := Verify(fresh, oldSecret); err == nil {
		t.Error("fresh token must NOT verify under the retired secret")
	}

	// 3. Once the rotation window closes and only the new secret is deployed,
	//    the old population is rejected.
	finished, err := ParseKeyring(newSecret)
	if err != nil {
		t.Fatalf("ParseKeyring: %v", err)
	}
	if _, err := finished.Verify(oldToken); err == nil {
		t.Error("after rotation completes, old tokens must be rejected")
	}
}

func TestKeyringVerify(t *testing.T) {
	valid, err := Sign("u1", "k2", time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	expired, err := Sign("u1", "k1", -time.Hour)
	if err != nil {
		t.Fatalf("sign expired: %v", err)
	}

	tests := []struct {
		name    string
		spec    string
		token   string
		wantErr string
	}{
		{name: "first key matches", spec: "k2,k1", token: valid},
		{name: "second key matches", spec: "k1,k2", token: valid},
		{name: "third key matches", spec: "kx,ky,k2", token: valid},
		{name: "no key matches", spec: "kx,ky", token: valid, wantErr: "invalid signature"},
		{name: "expired short-circuits", spec: "k1,k2", token: expired, wantErr: "token expired"},
		{name: "malformed token", spec: "k1", token: "not.a.jwt", wantErr: "invalid"},
		{name: "empty token", spec: "k1", token: "", wantErr: "invalid token format"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			k, err := ParseKeyring(tt.spec)
			if err != nil {
				t.Fatalf("ParseKeyring: %v", err)
			}
			_, err = k.Verify(tt.token)
			if tt.wantErr == "" {
				if err != nil {
					t.Fatalf("Verify() error: %v", err)
				}
				return
			}
			if err == nil {
				t.Fatalf("Verify() should fail with %q", tt.wantErr)
			}
			if !strings.Contains(err.Error(), tt.wantErr) {
				t.Errorf("Verify() error = %v, want it to contain %q", err, tt.wantErr)
			}
		})
	}
}

// TestKeyringVerifyOrder pins the short-circuit: an expired-but-valid signature
// must not be re-tried against the remaining keys, because the answer cannot
// change and the retry only costs HMACs.
func TestKeyringExpiredReturnsClaims(t *testing.T) {
	expired, err := SignWithServer("u9", "srv-1", "k1", -time.Minute)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	k, _ := ParseKeyring("k1,k2")
	claims, err := k.Verify(expired)
	if err == nil {
		t.Fatal("expired token must not verify")
	}
	// The claims come back populated so callers can log who it was.
	if claims.UserID != "u9" || claims.ServerID != "srv-1" {
		t.Errorf("claims = %+v, want the decoded payload alongside the error", claims)
	}
}

func TestZeroKeyringFailsClosed(t *testing.T) {
	var k Keyring
	if k.Valid() {
		t.Error("zero Keyring must be invalid")
	}
	if k.Signing() != "" {
		t.Error("zero Keyring must have no signing secret")
	}
	if _, err := k.Sign("u", time.Hour); err == nil {
		t.Error("zero Keyring must refuse to sign")
	}
	// Fail-closed matters most here: a service booted without a secret must
	// reject every token rather than accept tokens signed with "".
	tok, _ := Sign("u", "", time.Hour)
	if _, err := k.Verify(tok); err == nil {
		t.Error("zero Keyring must reject every token")
	}
}

func TestNewKeyring(t *testing.T) {
	k, err := NewKeyring("a", "b")
	if err != nil {
		t.Fatalf("NewKeyring: %v", err)
	}
	if k.Len() != 2 || k.Signing() != "a" {
		t.Errorf("NewKeyring(a,b) = %d keys signing %q, want 2 signing \"a\"", k.Len(), k.Signing())
	}
	if _, err := NewKeyring(); err == nil {
		t.Error("NewKeyring() with no secrets must fail")
	}
}
