package transfer

import (
	"testing"
)

func TestGenerateSessionKey_Length(t *testing.T) {
	key, err := GenerateSessionKey()
	if err != nil {
		t.Fatalf("GenerateSessionKey() error: %v", err)
	}
	if len(key) != SessionKeySize {
		t.Errorf("len = %d, want %d", len(key), SessionKeySize)
	}
}

func TestGenerateSessionKey_Entropy(t *testing.T) {
	a, err := GenerateSessionKey()
	if err != nil {
		t.Fatalf("GenerateSessionKey() error: %v", err)
	}
	b, err := GenerateSessionKey()
	if err != nil {
		t.Fatalf("GenerateSessionKey() error: %v", err)
	}
	if string(a) == string(b) {
		t.Error("two session keys are identical; crypto/rand may be broken")
	}
}

func TestGenerateSessionKey_NotAllZeros(t *testing.T) {
	key, err := GenerateSessionKey()
	if err != nil {
		t.Fatalf("GenerateSessionKey() error: %v", err)
	}
	allZero := true
	for _, b := range key {
		if b != 0 {
			allZero = false
			break
		}
	}
	if allZero {
		t.Error("session key is all zeros; crypto/rand may be broken")
	}
}
