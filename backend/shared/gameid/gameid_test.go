package gameid

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

func TestValidGameID(t *testing.T) {
	tests := []struct {
		id   string
		want bool
	}{
		{constants.DefaultGameID, true},
		{"gs", false}, {"map", false}, {"dungeon", false},
		{"gsx", true}, {"maps", true}, {"dungeon1", true},
		{"a", false},
		{"ab", true},
		{strings.Repeat("a", 24), true},
		{strings.Repeat("a", 25), false},
		{"", false},
		{"a0z9", true},
		{"1abc", false},
		{"Cuvara", false},
		{"aB", false},
		{"-", false},
		{"_", false},
		{":", false},
		{"/", false},
		{".", false},
		{"foo-bar", false},
		{"foo_bar", false},
		{"ab:c", false},
		{"ab/c", false},
		{"ab.c", false},
		{constants.DefaultGameID + "\n", false},
		{" ab", false},
		{"ab ", false},
		{"a b", false},
		{"ab\t", false},
		{"caf\u00e9", false},
		{"ab\uff11", false},
	}
	for _, tt := range tests {
		t.Run(fmt.Sprintf("%q", tt.id), func(t *testing.T) {
			if got := ValidGameID(tt.id); got != tt.want {
				t.Errorf("ValidGameID(%q) = %t, want %t", tt.id, got, tt.want)
			}
		})
	}
}

func TestValidMapID(t *testing.T) {
	tests := []struct {
		id   string
		want bool
	}{
		{"map_01", true},
		{"dungeon-crypt", true},
		{"A", true},
		{"ab", true},
		{"0", true},
		{"-", true},
		{"_", true},
		{strings.Repeat("a", 24), true},
		{strings.Repeat("a", 25), true},
		{strings.Repeat("a", 64), true},
		{strings.Repeat("a", 65), false},
		{"", false},
		{":", false},
		{"/", false},
		{"a:b", false},
		{"a/b", false},
		{".", false},
		{"a.b", false},
		{" ", false},
		{" map_01", false},
		{"map_01 ", false},
		{"map_01\n", false},
		{"map\t01", false},
		{"caf\u00e9", false},
		{"ab\uff11", false},
	}
	for _, tt := range tests {
		t.Run(fmt.Sprintf("%q", tt.id), func(t *testing.T) {
			if got := ValidMapID(tt.id); got != tt.want {
				t.Errorf("ValidMapID(%q) = %t, want %t", tt.id, got, tt.want)
			}
		})
	}
}

func TestIsDefaultGame(t *testing.T) {
	tests := []struct {
		game string
		want bool
	}{
		{"", true},
		{constants.DefaultGameID, true},
		{"foo", false},
		{"Cuvara", false},
		{constants.DefaultGameID + " ", false},
		{" ", false},
	}
	for _, tt := range tests {
		t.Run(fmt.Sprintf("%q", tt.game), func(t *testing.T) {
			if got := IsDefaultGame(tt.game); got != tt.want {
				t.Errorf("IsDefaultGame(%q) = %t, want %t", tt.game, got, tt.want)
			}
		})
	}
}

func TestValidServerID(t *testing.T) {
	for _, game := range []string{"", constants.DefaultGameID} {
		for _, id := range []string{
			"gs-0123456789", "gs-dotnet-map_01", "abc",
			"map-servers-dotnet-k8s-abcde-fghij", "foo-1", " ", "a:/\n\u00e9",
		} {
			t.Run(fmt.Sprintf("default/%q/%q", game, id), func(t *testing.T) {
				if !ValidServerID(game, id) {
					t.Errorf("ValidServerID(%q, %q) rejected a non-empty default-game id", game, id)
				}
			})
		}
	}
	tests := []struct {
		game string
		id   string
		want bool
	}{
		{"", "", false},
		{constants.DefaultGameID, "", false},
		{"foo", "foo-1", true},
		{"map", "map-1", false},
		{"gs", "gs-1", false},
		{"foo", "foo-", false},
		{"foo", "foo", false},
		{"foo", "foobar-1", false},
		{"foo", "gs-1", false},
		{"foo", "Foo-1", false},
		{"foo", "", false},
		{"foo", "foo-/", true},
		{"foo", "foo-\n", true},
		{"foobar", "foo-1", false},
		{"foobar", "foobar-1", true},
		{"Foo", "Foo-1", false},
		{"foo-bar", "foo-bar-1", false},
		{"a", "a-1", false},
		{"ab", "ab-1", true},
		{strings.Repeat("a", 24), strings.Repeat("a", 24) + "-1", true},
		{strings.Repeat("a", 25), strings.Repeat("a", 25) + "-1", false},
	}
	for _, tt := range tests {
		t.Run(fmt.Sprintf("%q/%q", tt.game, tt.id), func(t *testing.T) {
			if got := ValidServerID(tt.game, tt.id); got != tt.want {
				t.Errorf("ValidServerID(%q, %q) = %t, want %t", tt.game, tt.id, got, tt.want)
			}
		})
	}
}

// This fixture is also consumed by the C# tests; neither side may silently
// change the default identity or the accepted formats independently.
func TestIdentityFixtureParity(t *testing.T) {
	data, err := os.ReadFile("../testdata/multigame/identity.json")
	if err != nil {
		t.Fatal(fmt.Errorf("read shared identity fixture: %w", err))
	}
	var fixture struct {
		DefaultGameID string `json:"default_game_id"`
		GameIDs       []struct {
			ID    string `json:"id"`
			Valid bool   `json:"valid"`
		} `json:"game_ids"`
		MapIDs []struct {
			ID    string `json:"id"`
			Valid bool   `json:"valid"`
		} `json:"map_ids"`
		ServerIDs []struct {
			Game  string `json:"game"`
			ID    string `json:"id"`
			Valid bool   `json:"valid"`
		} `json:"server_ids"`
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(fmt.Errorf("decode shared identity fixture: %w", err))
	}
	if fixture.DefaultGameID != constants.DefaultGameID {
		t.Errorf("fixture default_game_id = %q, want %q", fixture.DefaultGameID, constants.DefaultGameID)
	}
	if len(fixture.GameIDs) == 0 || len(fixture.MapIDs) == 0 || len(fixture.ServerIDs) == 0 {
		t.Fatal("shared identity fixture must contain game_ids, map_ids and server_ids cases")
	}
	for _, tc := range fixture.GameIDs {
		t.Run(fmt.Sprintf("game/%q", tc.ID), func(t *testing.T) {
			if got := ValidGameID(tc.ID); got != tc.Valid {
				t.Errorf("ValidGameID(%q) = %t, fixture wants %t", tc.ID, got, tc.Valid)
			}
		})
	}
	for _, tc := range fixture.MapIDs {
		t.Run(fmt.Sprintf("map/%q", tc.ID), func(t *testing.T) {
			if got := ValidMapID(tc.ID); got != tc.Valid {
				t.Errorf("ValidMapID(%q) = %t, fixture wants %t", tc.ID, got, tc.Valid)
			}
		})
	}
	for _, tc := range fixture.ServerIDs {
		t.Run(fmt.Sprintf("server/%q/%q", tc.Game, tc.ID), func(t *testing.T) {
			if got := ValidServerID(tc.Game, tc.ID); got != tc.Valid {
				t.Errorf("ValidServerID(%q, %q) = %t, fixture wants %t", tc.Game, tc.ID, got, tc.Valid)
			}
		})
	}
}
