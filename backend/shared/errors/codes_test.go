package errors

import (
	"testing"
)

func TestGameError_Error(t *testing.T) {
	tests := []struct {
		code ErrorCode
		msg  string
		want string
	}{
		{ErrUnauthorized, "bad token", "[UNAUTHORIZED] bad token"},
		{ErrServerFull, "map_01", "[SERVER_FULL] map_01"},
		{ErrorCode(9999), "unknown", "[UNKNOWN] unknown"},
	}
	for _, tt := range tests {
		err := New(tt.code, tt.msg)
		if err.Error() != tt.want {
			t.Errorf("New(%d, %q).Error() = %q, want %q", tt.code, tt.msg, err.Error(), tt.want)
		}
	}
}

func TestNewf(t *testing.T) {
	err := Newf(ErrCooldown, "skill %s: %ds left", "fireball", 3)
	want := "[COOLDOWN] skill fireball: 3s left"
	if err.Error() != want {
		t.Errorf("Newf() = %q, want %q", err.Error(), want)
	}
}

func TestIs(t *testing.T) {
	err := New(ErrSpeedHack, "too fast")
	if !Is(err, ErrSpeedHack) {
		t.Error("Is() should match ErrSpeedHack")
	}
	if Is(err, ErrUnauthorized) {
		t.Error("Is() should not match ErrUnauthorized")
	}
}
