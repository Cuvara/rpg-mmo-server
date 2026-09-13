package redisstore

import (
	"context"
	"crypto/ed25519"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/sealed"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// The Go reader's half of the cross-language registry contract (ADR-25). The C#
// writer's half is GameServer.Tests/Registry/RedisServerRegistryTests.cs, which
// pins the same field name and the same base64 against a real Redis.
//
// These two tests are what a drift would break: rename the field on one side and
// the gateway silently reads an empty key, so every identity-requiring client
// refuses a server that is perfectly capable of proving itself — with nothing in
// any log naming the registry.
func TestRegistry_IdentityKeyRoundTripsThroughTheHash(t *testing.T) {
	_, reg := newTestRegistry(t)
	ctx := context.Background()

	pub, _, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	info := testServer("gs-identity", "map_identity")
	info.IdentityKey = sealed.EncodeIdentityKey(pub)

	if err := reg.Register(ctx, info); err != nil {
		t.Fatal(err)
	}

	got, err := reg.GetServer(ctx, "gs-identity")
	if err != nil {
		t.Fatal(err)
	}
	if got.IdentityKey != info.IdentityKey {
		t.Fatalf("IdentityKey = %q, want %q", got.IdentityKey, info.IdentityKey)
	}

	// And it decodes back to the same 32 bytes, which is what the gateway
	// actually forwards.
	raw, err := sealed.DecodeIdentityKey(got.IdentityKey)
	if err != nil {
		t.Fatal(err)
	}
	if string(raw) != string(pub) {
		t.Fatal("the key did not survive the registry round trip")
	}

	// The map lookup path is a separate read (HGetAll per member), so it is
	// asserted separately rather than assumed to match GetServer.
	found, err := reg.FindByMapID(ctx, "map_identity")
	if err != nil {
		t.Fatal(err)
	}
	if len(found) != 1 || found[0].IdentityKey != info.IdentityKey {
		t.Fatalf("FindByMapID lost the identity key: %+v", found)
	}
}

// A pre-ADR-25 entry has no such field at all. Reading it must yield an empty
// key and no error: the gateway assigns players to that server as before, and
// only a client that REQUIRES identity refuses, one hop later.
func TestRegistry_MissingIdentityKeyReadsAsEmptyNotAnError(t *testing.T) {
	mr, reg := newTestRegistry(t)
	ctx := context.Background()

	// Written by hand, exactly as a server predating this field would have: no
	// identity_key member in the hash.
	key := "servers:id:gs-legacy"
	mr.HSet(key, "server_id", "gs-legacy", "map_id", "map_legacy",
		"addr", "10.0.0.9:9000", "transport", "tcp", "capacity", "100", "player_count", "3")
	mr.SetAdd("servers:map:map_legacy", "gs-legacy")

	got, err := reg.GetServer(ctx, "gs-legacy")
	if err != nil {
		t.Fatalf("a pre-ADR-25 entry failed to read: %v", err)
	}
	if got.IdentityKey != "" {
		t.Fatalf("IdentityKey = %q, want empty", got.IdentityKey)
	}
	// Everything else is still intact — the point is that the new field changed
	// nothing about how an old entry parses.
	if got.Addr != "10.0.0.9:9000" || got.PlayerCount != 3 {
		t.Fatalf("the rest of the entry did not survive: %+v", got)
	}
	var _ storage.ServerInfo = got
}
