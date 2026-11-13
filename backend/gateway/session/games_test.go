package session

import (
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

func TestParseGames(t *testing.T) {
	for _, tc := range []struct {
		raw     string
		want    []string
		invalid bool
	}{
		{"", []string{"cuvara"}, false}, {"  ", []string{"cuvara"}, false},
		{"cuvara", []string{"cuvara"}, false}, {"cuvara,foo", []string{"cuvara", "foo"}, false},
		{" foo , cuvara ,foo", []string{"foo", "cuvara"}, false}, {"cuvara,", nil, true},
		{"Foo", nil, true}, {"gs", nil, true}, {"foo:bar", nil, true},
	} {
		t.Run(tc.raw, func(t *testing.T) {
			games, err := ParseGames(tc.raw)
			if tc.invalid {
				if !errors.Is(err, ErrInvalidGames) || !strings.Contains(err.Error(), EnvGames) {
					t.Fatalf("error = %v", err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got := games.IDs(); !reflect.DeepEqual(got, tc.want) {
				t.Errorf("IDs = %v, want %v", got, tc.want)
			}
			for _, game := range []string{"", "cuvara", "foo", "bar"} {
				want := false
				for _, id := range tc.want {
					if id == game {
						want = true
					}
				}
				if got := games.Allowed(game); got != want {
					t.Errorf("Allowed(%q) = %v, want %v", game, got, want)
				}
			}
			ids := games.IDs()
			ids[0] = "changed"
			if games.IDs()[0] != tc.want[0] {
				t.Error("IDs exposes internal slice")
			}
		})
	}
}

func TestZeroGames(t *testing.T) {
	var games Games
	if !reflect.DeepEqual(games.IDs(), []string{constants.DefaultGameID}) || !games.Allowed(constants.DefaultGameID) || games.Allowed("") || games.Allowed("foo") {
		t.Error("zero Games does not behave as default")
	}
}
