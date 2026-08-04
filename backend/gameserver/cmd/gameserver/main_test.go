package main

import "testing"

// TestResolveServerID pins the server-id contract: a pod must register under
// its GameServer (pod) name so the gateway's join token sid matches.
func TestResolveServerID(t *testing.T) {
	tests := []struct {
		name  string
		env   map[string]string
		mode  string
		mapID string
		want  string
	}{
		{
			name:  "falls back to gs-<mode>-<map> outside kubernetes",
			mode:  "map",
			mapID: "map_01",
			want:  "gs-map-map_01",
		},
		{
			name:  "POD_NAME wins over the default",
			env:   map[string]string{"POD_NAME": "map-servers-dev-xjh7p-6ndtl"},
			mode:  "map",
			mapID: "map_01",
			want:  "map-servers-dev-xjh7p-6ndtl",
		},
		{
			name:  "GAMESERVER_ID wins over POD_NAME",
			env:   map[string]string{"GAMESERVER_ID": "explicit-id", "POD_NAME": "pod-1"},
			mode:  "dungeon",
			mapID: "dungeon_01",
			want:  "explicit-id",
		},
		{
			name:  "empty env vars are ignored",
			env:   map[string]string{"GAMESERVER_ID": "", "POD_NAME": ""},
			mode:  "dungeon",
			mapID: "dungeon_01",
			want:  "gs-dungeon-dungeon_01",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// Neutralise anything inherited from the real environment.
			t.Setenv("GAMESERVER_ID", "")
			t.Setenv("POD_NAME", "")
			for k, v := range tt.env {
				t.Setenv(k, v)
			}
			if got := resolveServerID(tt.mode, tt.mapID); got != tt.want {
				t.Errorf("resolveServerID(%q, %q) = %q, want %q", tt.mode, tt.mapID, got, tt.want)
			}
		})
	}
}
