package server

import (
	"encoding/json"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

func TestSessionSupersededEventJSON(t *testing.T) {
	tests := []struct {
		name   string
		gameID string
		want   string
	}{
		{"absent game", "", `{"user_id":"u1","server_id":"srv1","jti":"j","old_gateway":"gw-a","new_gateway":"gw-b"}`},
		{"named game", "foo", `{"user_id":"u1","server_id":"srv1","jti":"j","old_gateway":"gw-a","new_gateway":"gw-b","game_id":"foo"}`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			event := SessionSupersededEvent{UserID: "u1", ServerID: "srv1", JTI: "j", OldGateway: "gw-a", NewGateway: "gw-b", GameID: tt.gameID}
			got, err := json.Marshal(event)
			if err != nil {
				t.Fatal(err)
			}
			if string(got) != tt.want {
				t.Errorf("json.Marshal() = %s, want %s", got, tt.want)
			}
		})
	}
}

func TestSessionSupersededEventUnmarshalWithoutGame(t *testing.T) {
	var event SessionSupersededEvent
	if err := json.Unmarshal([]byte(`{"user_id":"u1","server_id":"srv1","jti":"j"}`), &event); err != nil {
		t.Fatal(err)
	}
	if event.GameID != "" || event.Game() != constants.DefaultGameID {
		t.Errorf("GameID = %q, Game() = %q; want empty and %q", event.GameID, event.Game(), constants.DefaultGameID)
	}
}
