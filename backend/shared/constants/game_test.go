package constants

import "testing"

func TestGameOrDefault(t *testing.T) {
	tests := []struct {
		name string
		id   string
		want string
	}{
		{"absent", "", DefaultGameID},
		{"explicit default", DefaultGameID, DefaultGameID},
		{"other game", "foobar", "foobar"},
		{"space is not absent", " ", " "},
		{"no trimming", " foo ", " foo "},
		{"no validation", "Foo/bar\n", "Foo/bar\n"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := GameOrDefault(tt.id); got != tt.want {
				t.Errorf("GameOrDefault(%q) = %q, want %q", tt.id, got, tt.want)
			}
		})
	}
}
