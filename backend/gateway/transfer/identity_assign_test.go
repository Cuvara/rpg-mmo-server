package transfer

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"testing"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/shared/sealed"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

func registered(t *testing.T, info storage.ServerInfo) *registry.RegistryService {
	t.Helper()
	reg := registry.NewRegistryService(storage.NewMemoryServerRegistry())
	if err := reg.RegisterServer(context.Background(), info); err != nil {
		t.Fatal(err)
	}
	return reg
}

// The relay, end to end inside the gateway: a pod publishes an identity key, and
// the client's assignment carries it. Everything else in ADR-25 is downstream of
// this hop working.
func TestAssignMapCarriesTheServersIdentityKey(t *testing.T) {
	pub, _, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	reg := registered(t, storage.ServerInfo{
		ServerID:    "srv-identity",
		MapID:       "map_identity",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		IdentityKey: sealed.EncodeIdentityKey(pub),
	})

	result, err := AssignMap(context.Background(), "user1", "map_identity", reg, "test-secret")
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(result.ServerPublicKey, pub) {
		t.Fatalf("ServerPublicKey = %x, want %x — the gateway must relay the pod's key "+
			"verbatim; anything else and the client's signature check cannot pass",
			result.ServerPublicKey, pub)
	}
	if len(result.ServerPublicKey) != sealed.IdentityKeySize {
		t.Fatalf("key is %d bytes, want %d", len(result.ServerPublicKey), sealed.IdentityKeySize)
	}
}

// A server older than ADR-25 publishes no key, and the assignment must SUCCEED
// anyway with an empty one. This is the migration order in ADR-25 §5: the
// gateway and server ship first, and a gateway that refused to assign an
// identity-less server would take every un-upgraded map offline at deploy time.
func TestAssignMapSucceedsWithNoIdentityKey(t *testing.T) {
	reg := registered(t, storage.ServerInfo{
		ServerID: "srv-old", MapID: "map_old", Addr: "10.0.0.2:9000", Capacity: 100,
	})

	result, err := AssignMap(context.Background(), "user1", "map_old", reg, "test-secret")
	if err != nil {
		t.Fatalf("an identity-less server was refused an assignment: %v", err)
	}
	if result.ServerAddr == "" || result.JoinToken == "" {
		t.Fatal("the assignment itself did not complete")
	}
	if len(result.ServerPublicKey) != 0 {
		t.Fatalf("ServerPublicKey = %x, want empty", result.ServerPublicKey)
	}
}

// A registry entry carrying garbage must also not fail the assignment — it
// degrades to "no key", and the refusal happens one hop later at the sealed
// handshake, where the error can actually name encryption. Failing here would
// turn one bad write into a map-wide outage.
func TestAssignMapTreatsAMalformedIdentityKeyAsAbsent(t *testing.T) {
	for name, bad := range map[string]string{
		"not base64":    "!!!! not base64 !!!!",
		"wrong length":  sealed.EncodeIdentityKey(make([]byte, 31)),
		"empty padding": "====",
	} {
		t.Run(name, func(t *testing.T) {
			reg := registered(t, storage.ServerInfo{
				ServerID: "srv-bad", MapID: "map_bad", Addr: "10.0.0.3:9000",
				Capacity: 100, IdentityKey: bad,
			})

			result, err := AssignMap(context.Background(), "user1", "map_bad", reg, "test-secret")
			if err != nil {
				t.Fatalf("a malformed key failed the whole assignment: %v", err)
			}
			if len(result.ServerPublicKey) != 0 {
				t.Fatalf("ServerPublicKey = %x, want empty — a key that cannot be "+
					"decoded must never be forwarded half-decoded", result.ServerPublicKey)
			}
		})
	}
}
