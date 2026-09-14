package sessionkey

import (
	"bytes"
	"encoding/json"
	"fmt"
	"log/slog"
	"strings"
	"testing"
)

const testSecret = "join-secret-for-tests"

func TestDeriveIsDeterministicAndPerSession(t *testing.T) {
	a, err := Derive(testSecret, "jti-one")
	if err != nil {
		t.Fatal(err)
	}
	again, err := Derive(testSecret, "jti-one")
	if err != nil {
		t.Fatal(err)
	}
	other, err := Derive(testSecret, "jti-two")
	if err != nil {
		t.Fatal(err)
	}

	// Determinism is what lets both ends compute the key without transmitting it.
	if !bytes.Equal(a.Bytes(), again.Bytes()) {
		t.Error("same secret and jti produced different keys; the two ends could never agree")
	}
	// Per-session is the entire point: a fresh jti per join means a fresh key.
	if bytes.Equal(a.Bytes(), other.Bytes()) {
		t.Error("different jti produced the same key; the key is not per-session")
	}
	if len(a.Bytes()) != Size {
		t.Errorf("key length = %d, want %d", len(a.Bytes()), Size)
	}
}

func TestDeriveDependsOnTheSecret(t *testing.T) {
	a, _ := Derive(testSecret, "jti-one")
	b, _ := Derive("a-different-secret", "jti-one")
	if bytes.Equal(a.Bytes(), b.Bytes()) {
		t.Error("key did not change with the secret; it is not rooted in JOIN_TOKEN_SECRET")
	}
}

// A rotation list must derive from the CURRENT entry — the same one used to
// mint — or the gateway and the game server would disagree during a rotation.
func TestDeriveUsesTheCurrentKeyOfARotationList(t *testing.T) {
	single, _ := Derive("current", "jti-one")
	rotating, _ := Derive("current,previous", "jti-one")
	if !bytes.Equal(single.Bytes(), rotating.Bytes()) {
		t.Error("rotation list did not derive from the first entry")
	}
	spaced, _ := Derive("  current , previous ", "jti-one")
	if !bytes.Equal(single.Bytes(), spaced.Bytes()) {
		t.Error("whitespace in a rotation list changed the derived key")
	}
}

func TestDeriveRefusesWithoutSecretOrJTI(t *testing.T) {
	if _, err := Derive("", "jti"); err != ErrNoSecret {
		t.Errorf("Derive with no secret: err = %v, want ErrNoSecret", err)
	}
	if _, err := Derive(testSecret, ""); err != ErrNoJTI {
		t.Errorf("Derive with no jti: err = %v, want ErrNoJTI", err)
	}
}

// The realistic way a secret escapes is not a deliberate log call — it is a
// struct that gets formatted, reflected over, or JSON-encoded by something that
// did not know it held a key. Every one of those paths must redact.
func TestKeyNeverRendersItsMaterial(t *testing.T) {
	k, err := Derive(testSecret, "jti-one")
	if err != nil {
		t.Fatal(err)
	}
	material := k.Hex()

	var buf bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&buf, nil))
	logger.Info("carrying a key", "key", k)

	wrapper := struct {
		Key Key `json:"key"`
	}{Key: k}
	encoded, err := json.Marshal(wrapper)
	if err != nil {
		t.Fatal(err)
	}

	renders := map[string]string{
		"fmt %v":       sprint("%v", k),
		"fmt %s":       sprint("%s", k),
		"fmt %#v":      sprint("%#v", k),
		"fmt %+v":      sprint("%+v", k),
		"slog":         buf.String(),
		"json.Marshal": string(encoded),
	}
	for where, got := range renders {
		if strings.Contains(got, material) {
			t.Errorf("%s leaked the key material: %s", where, got)
		}
		if !strings.Contains(got, Redacted) {
			t.Errorf("%s did not render the redaction marker: %s", where, got)
		}
	}
}

// The info string is part of the wire contract: change it and the two ends
// derive different keys and no session forms, with no error that names the
// cause. Pinned so that is a deliberate act.
func TestInfoStringIsPartOfTheContract(t *testing.T) {
	if Info != "cuvara/session-key/v1" {
		t.Errorf("Info = %q; changing it silently breaks every peer", Info)
	}
	if Size != 32 {
		t.Errorf("Size = %d, want 32", Size)
	}
}

// A golden vector, so the C# implementation can be checked against this one
// without running both. Cross-implementation agreement is what makes derivation
// safe to do independently on each end.
func TestGoldenVector(t *testing.T) {
	k, err := Derive("golden-secret", "golden-jti")
	if err != nil {
		t.Fatal(err)
	}
	const want = "3d79a9aa8d3284392d4d5cde4e968e30e2fb819689e5e0e38c51ec86e334176d"
	if got := k.Hex(); got != want {
		t.Errorf("golden vector changed\n got: %s\nwant: %s\n"+
			"If this is intentional, the C# side and any client must move with it.", got, want)
	}
}

func sprint(format string, k Key) string { return fmt.Sprintf(format, k) }
