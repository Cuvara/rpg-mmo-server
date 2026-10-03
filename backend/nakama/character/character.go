// Package character implements the character roster RPCs (ADR-31) for the
// Nakama Go runtime plugin: character_list, character_create and
// character_delete.
//
// # Ownership (ADR-1, ADR-31)
//
// Nakama owns the ROSTER: which characters an account has, their slot, name and
// creation time. It does not own character STATE (position, HP, level, bag,
// equipment): that is the game server's, in the game-state database, keyed by
// the character id minted here. Nothing in this package touches that database
// or makes any outbound call; deleting a character here leaves its game-state
// rows to be retired by the game-state side.
//
// # Storage model
//
//	collection "characters", key "roster", owner <user> -> Roster
//
// One object per user holding the whole roster, rather than one object per
// character, because every rule that matters is a rule about the roster as a
// whole (slot count, slot uniqueness). A single object makes each mutation one
// version-checked write: two concurrent creates cannot both take the last slot
// or the same slot, because the loser's write carries a stale version, is
// rejected by Nakama, re-reads and re-validates. Permissions: owner may read
// (a client can show its roster without an RPC), nobody may write except these
// RPCs.
package character

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"regexp"
	"sort"
	"time"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPC names registered from main.go.
const (
	RPCCharacterList   = "character_list"
	RPCCharacterCreate = "character_create"
	RPCCharacterDelete = "character_delete"
)

// Storage location of a user's roster. See the package doc.
const (
	// Collection holds one roster object per user.
	Collection = "characters"
	// RosterKey is the single key inside Collection.
	RosterKey = "roster"
)

// Character-creation limits. ALL OF THESE ARE PLACEHOLDERS (ADR-31
// "Consequences"): slot count and name rules are content limits that design
// has not supplied yet. They exist so the plumbing can be built and tested;
// change them when design decides, and update nakama/docs/API.md with them.
const (
	// MaxCharacterSlots is the number of roster slots per account.
	// PLACEHOLDER pending design.
	MaxCharacterSlots = 4
	// NameMinLength is the shortest accepted character name, in bytes (the
	// placeholder alphabet is ASCII, so bytes == characters).
	// PLACEHOLDER pending design.
	NameMinLength = 3
	// NameMaxLength is the longest accepted character name, in bytes.
	// PLACEHOLDER pending design.
	NameMaxLength = 16
	// NamePatternPlaceholder is the accepted alphabet: ASCII letters, digits
	// and underscore. PLACEHOLDER pending design (no localisation, no
	// uniqueness, no profanity filter yet).
	NamePatternPlaceholder = "^[A-Za-z0-9_]+$"
)

var namePattern = regexp.MustCompile(NamePatternPlaceholder)

// maxWriteAttempts bounds the optimistic-concurrency retry loop. A retry only
// happens when another request for the SAME user changed the roster between
// our read and write, which a single client does not do in practice.
const maxWriteAttempts = 5

// Character is one roster entry.
type Character struct {
	// ID is a random UUID v4. It is the key of the character's state in the
	// game-state DB and the value of the `cid` JWT claim.
	ID string `json:"id"`
	// Slot is the roster position, 0 <= Slot < MaxCharacterSlots, unique per
	// account.
	Slot      int    `json:"slot"`
	Name      string `json:"name"`
	CreatedAt int64  `json:"created_at"`
}

// Roster is the stored per-user object.
type Roster struct {
	Characters []Character `json:"characters"`
}

// Store is the narrow slice of runtime.NakamaModule the roster logic uses.
// runtime.NakamaModule satisfies it; tests use a version-aware fake.
type Store interface {
	StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error)
	StorageWrite(ctx context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error)
}

// ValidateName applies the placeholder name rules: length NameMinLength to
// NameMaxLength, alphabet NamePatternPlaceholder.
func ValidateName(name string) error {
	if len(name) < NameMinLength || len(name) > NameMaxLength || !namePattern.MatchString(name) {
		return ErrInvalidName
	}
	return nil
}

// newCharacterID returns a random UUID v4 string. crypto/rand, not math/rand:
// the id ends up in a bearer token and must not be guessable.
func newCharacterID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", fmt.Errorf("generate character id: %w", err)
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // RFC 4122 variant
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), nil
}

// ReadRoster loads a user's roster and its storage version. A user with no
// roster object gets an empty roster and version "" (the next write is then
// create-only).
func ReadRoster(ctx context.Context, nk Store, userID string) (Roster, string, error) {
	objs, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: Collection,
		Key:        RosterKey,
		UserID:     userID,
	}})
	if err != nil {
		return Roster{}, "", fmt.Errorf("read roster %s: %w", userID, err)
	}
	for _, o := range objs {
		if o.GetCollection() != Collection || o.GetKey() != RosterKey {
			continue
		}
		var r Roster
		if err := json.Unmarshal([]byte(o.GetValue()), &r); err != nil {
			return Roster{}, "", fmt.Errorf("corrupt roster %s: %w", userID, err)
		}
		return r, o.GetVersion(), nil
	}
	return Roster{}, "", nil
}

// writeRoster stores the roster at a known version: "*" (create-only) when
// none existed, otherwise the version read at the start of the attempt.
func writeRoster(ctx context.Context, nk Store, userID string, r Roster, version string) error {
	if version == "" {
		version = "*"
	}
	value, err := json.Marshal(r)
	if err != nil {
		return fmt.Errorf("marshal roster: %w", err)
	}
	if _, err := nk.StorageWrite(ctx, []*runtime.StorageWrite{{
		Collection:      Collection,
		Key:             RosterKey,
		UserID:          userID,
		Value:           string(value),
		Version:         version,
		PermissionRead:  1, // owner read
		PermissionWrite: 0, // server-authoritative: only these RPCs write
	}}); err != nil {
		return fmt.Errorf("write roster %s: %w", userID, err)
	}
	return nil
}

// sortedBySlot returns the characters ordered by slot.
func sortedBySlot(cs []Character) []Character {
	out := append([]Character(nil), cs...)
	sort.Slice(out, func(i, j int) bool { return out[i].Slot < out[j].Slot })
	return out
}

// List returns the caller's characters ordered by slot.
func List(ctx context.Context, nk Store, userID string) ([]Character, error) {
	if userID == "" {
		return nil, ErrUnauthenticated
	}
	r, _, err := ReadRoster(ctx, nk, userID)
	if err != nil {
		return nil, fmt.Errorf("list characters: %w", err)
	}
	return sortedBySlot(r.Characters), nil
}

// Owns reports whether characterID is in userID's roster. gateway_token calls
// it before putting a `cid` claim in a token.
func Owns(ctx context.Context, nk Store, userID, characterID string) (bool, error) {
	if userID == "" || characterID == "" {
		return false, nil
	}
	r, _, err := ReadRoster(ctx, nk, userID)
	if err != nil {
		return false, fmt.Errorf("check character ownership: %w", err)
	}
	for _, c := range r.Characters {
		if c.ID == characterID {
			return true, nil
		}
	}
	return false, nil
}

// Create adds a character to the caller's roster. slot < 0 means "lowest free
// slot"; otherwise the slot must be below MaxCharacterSlots and free.
func Create(ctx context.Context, nk Store, userID, name string, slot int, now func() time.Time) (Character, error) {
	if userID == "" {
		return Character{}, ErrUnauthenticated
	}
	if err := ValidateName(name); err != nil {
		return Character{}, err
	}
	if slot >= MaxCharacterSlots {
		return Character{}, ErrInvalidSlot
	}

	var lastErr error
	for attempt := 0; attempt < maxWriteAttempts; attempt++ {
		r, version, err := ReadRoster(ctx, nk, userID)
		if err != nil {
			return Character{}, fmt.Errorf("create character: %w", err)
		}
		if len(r.Characters) >= MaxCharacterSlots {
			return Character{}, ErrRosterFull
		}

		taken := make(map[int]bool, len(r.Characters))
		for _, c := range r.Characters {
			taken[c.Slot] = true
		}
		chosen := slot
		if chosen < 0 {
			for s := 0; s < MaxCharacterSlots; s++ {
				if !taken[s] {
					chosen = s
					break
				}
			}
		} else if taken[chosen] {
			return Character{}, ErrSlotTaken
		}

		id, err := newCharacterID()
		if err != nil {
			return Character{}, fmt.Errorf("create character: %w", err)
		}
		c := Character{ID: id, Slot: chosen, Name: name, CreatedAt: now().Unix()}
		updated := Roster{Characters: append(append(make([]Character, 0, len(r.Characters)+1), r.Characters...), c)}
		if err := writeRoster(ctx, nk, userID, updated, version); err != nil {
			lastErr = err
			continue // lost a version check (or a transient failure): re-read, re-validate
		}
		return c, nil
	}
	return Character{}, fmt.Errorf("create character after %d attempts: %w: %w", maxWriteAttempts, ErrRosterBusy, lastErr)
}

// Delete removes a character from the caller's roster. It does not touch the
// character's game-state rows (owned by the game server, ADR-1/ADR-31).
func Delete(ctx context.Context, nk Store, userID, characterID string) error {
	if userID == "" {
		return ErrUnauthenticated
	}
	if characterID == "" {
		return ErrCharacterIDRequired
	}

	var lastErr error
	for attempt := 0; attempt < maxWriteAttempts; attempt++ {
		r, version, err := ReadRoster(ctx, nk, userID)
		if err != nil {
			return fmt.Errorf("delete character: %w", err)
		}
		remaining := make([]Character, 0, len(r.Characters))
		for _, c := range r.Characters {
			if c.ID != characterID {
				remaining = append(remaining, c)
			}
		}
		if len(remaining) == len(r.Characters) {
			return ErrCharacterNotFound
		}
		if err := writeRoster(ctx, nk, userID, Roster{Characters: remaining}, version); err != nil {
			lastErr = err
			continue
		}
		return nil
	}
	return fmt.Errorf("delete character after %d attempts: %w: %w", maxWriteAttempts, ErrRosterBusy, lastErr)
}
