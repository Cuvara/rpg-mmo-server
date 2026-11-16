package main

import (
	"errors"
	"fmt"
	"strings"

	sharedjwt "github.com/duycuong/rpg-mmo/shared/jwt"
)

// authKeyringSetting names the auth keyring in separation errors. It is the
// environment variable the gateway reads it from (config.Load).
const authKeyringSetting = "JWT_SECRET"

// errKeyringOverlap is wrapped by every checkKeyringSeparation error.
var errKeyringOverlap = errors.New("keyrings share a key")

// gameKeyring is one game's join-token keyring as configured: the game it
// mints join tokens for, the setting it was read from (JOIN_TOKEN_SECRET for
// the default game, JOIN_TOKEN_SECRET_<GAME> otherwise) and its raw value.
type gameKeyring struct {
	Game    string
	Setting string
	Spec    string
}

// keyringKeys returns the individual keys of a keyring setting with exactly the
// semantics of sharedjwt.ParseKeyring: split on sharedjwt.KeyringSeparator,
// trim whitespace, drop empty entries. ParseKeyring does not expose its keys,
// so this is the one place the gateway re-derives them; the parity test in
// keyring_separation_test.go keeps the two in step. An empty result means
// ParseKeyring would refuse the setting.
func keyringKeys(spec string) []string {
	var keys []string
	for _, part := range strings.Split(spec, sharedjwt.KeyringSeparator) {
		if k := strings.TrimSpace(part); k != "" {
			keys = append(keys, k)
		}
	}
	return keys
}

// checkKeyringSeparation refuses any key that sits in two trust domains
// (MG-02): the auth keyring (JWT_SECRET) and any game's join keyring, or the
// join keyrings of two different games. Keys are compared individually, so a
// rotation entry such as JWT_SECRET=new,old next to
// JOIN_TOKEN_SECRET_EMBER=join,old is caught. A key repeated inside one
// keyring is harmless and is not reported.
//
// Why fatal: a game-server pod holds its game's join keyring. If that keyring
// contains an auth key, compromising the pod lets an attacker mint a gateway
// auth token for any user and any game; if it contains another game's join
// key, the pod can mint that game's join tokens.
//
// The error names games and settings only. It never contains a key, a key's
// length or its position in the list, so it is safe to log. Violations are
// reported in the order of joins, which callers build in games.IDs() order.
func checkKeyringSeparation(auth string, joins []gameKeyring) error {
	authKeys := keySet(auth)
	joinKeys := make([]map[string]struct{}, len(joins))
	for i, j := range joins {
		joinKeys[i] = keySet(j.Spec)
	}

	var violations []string
	for i, j := range joins {
		if intersects(joinKeys[i], authKeys) {
			violations = append(violations, fmt.Sprintf("%s (game %q) shares a key with %s (auth)",
				j.Setting, j.Game, authKeyringSetting))
		}
	}
	for i := range joins {
		for k := i + 1; k < len(joins); k++ {
			if intersects(joinKeys[i], joinKeys[k]) {
				violations = append(violations, fmt.Sprintf("%s (game %q) shares a key with %s (game %q)",
					joins[i].Setting, joins[i].Game, joins[k].Setting, joins[k].Game))
			}
		}
	}
	if len(violations) == 0 {
		return nil
	}
	return fmt.Errorf("keyring separation: %w: %s", errKeyringOverlap, strings.Join(violations, "; "))
}

func keySet(spec string) map[string]struct{} {
	keys := keyringKeys(spec)
	set := make(map[string]struct{}, len(keys))
	for _, k := range keys {
		set[k] = struct{}{}
	}
	return set
}

func intersects(a, b map[string]struct{}) bool {
	if len(b) < len(a) {
		a, b = b, a
	}
	for k := range a {
		if _, ok := b[k]; ok {
			return true
		}
	}
	return false
}
