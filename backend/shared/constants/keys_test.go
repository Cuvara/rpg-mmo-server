package constants

import "testing"

func TestSessionKey(t *testing.T) {
	for _, tc := range []struct{ game, want string }{
		{"", "session:u1"}, {DefaultGameID, "session:u1"}, {"foo", "session:foo:u1"},
	} {
		t.Run(tc.game, func(t *testing.T) {
			if got := SessionKey(tc.game, "u1"); got != tc.want {
				t.Errorf("SessionKey(%q) = %q, want %q", tc.game, got, tc.want)
			}
		})
	}
	if got := SessionKey(DefaultGameID, "u1"); got != SessionKeyPrefix+"u1" {
		t.Errorf("default key = %q", got)
	}
}
