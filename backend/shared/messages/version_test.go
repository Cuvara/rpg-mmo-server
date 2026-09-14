package messages

import (
	"strings"
	"testing"
)

// The version handshake exists to turn a silent misparse into a named refusal.
// These tests pin the three things that promise depends on: the decision rule,
// the wire round-trip of the field in BOTH encodings, and the zero-elision
// behaviour that decides what an old peer looks like.

func TestCheckProtocolVersion(t *testing.T) {
	tests := []struct {
		name        string
		peerVersion uint32
		minVersion  uint32
		want        VersionVerdict
	}{
		{
			name:        "matching version is accepted",
			peerVersion: WireProtocolVersion,
			minVersion:  0,
			want:        VersionAccepted,
		},
		{
			name:        "matching version is accepted even when advertisement is required",
			peerVersion: WireProtocolVersion,
			minVersion:  1,
			want:        VersionAccepted,
		},
		{
			name:        "unversioned peer is admitted under the shipping default",
			peerVersion: ProtocolVersionUnversioned,
			minVersion:  0,
			want:        VersionAcceptedUnversioned,
		},
		{
			name:        "unversioned peer is refused once advertisement is required",
			peerVersion: ProtocolVersionUnversioned,
			minVersion:  1,
			want:        VersionRefused,
		},
		{
			// The interesting direction: a peer from the FUTURE is refused just
			// as firmly as one from the past. This build cannot know what a
			// later version changed, so admitting it would be the guess the
			// whole mechanism exists to prevent.
			name:        "newer peer is refused, not optimistically admitted",
			peerVersion: WireProtocolVersion + 1,
			minVersion:  0,
			want:        VersionRefused,
		},
		{
			name:        "much newer peer is refused",
			peerVersion: 9999,
			minVersion:  0,
			want:        VersionRefused,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := CheckProtocolVersion(tt.peerVersion, tt.minVersion)
			if got != tt.want {
				t.Fatalf("CheckProtocolVersion(%d, %d) = %v, want %v",
					tt.peerVersion, tt.minVersion, got, tt.want)
			}
		})
	}
}

// A version older than this build must be refused. Spelled out separately from
// the table because it is the case the field was added for, and it only exists
// once WireProtocolVersion has moved past 1 — before that there is no older
// non-zero version to construct.
func TestOlderPeerIsRefused(t *testing.T) {
	if WireProtocolVersion < 2 {
		t.Skip("no older non-zero version exists while WireProtocolVersion is 1")
	}
	if got := CheckProtocolVersion(WireProtocolVersion-1, 0); got != VersionRefused {
		t.Fatalf("older peer: got %v, want VersionRefused", got)
	}
}

// The reason string is what a client branches on, so it must stay the bare
// machine-readable token — no numbers, no prose, matching "duplicate_login".
func TestMismatchReasonIsABareToken(t *testing.T) {
	if ReasonProtocolVersionMismatch != "protocol_version_mismatch" {
		t.Fatalf("reason token changed: %q — clients branch on this string",
			ReasonProtocolVersionMismatch)
	}
	if strings.ContainsAny(ReasonProtocolVersionMismatch, " 0123456789") {
		t.Fatalf("reason token %q must not embed versions or prose; that detail belongs in the log",
			ReasonProtocolVersionMismatch)
	}
}

// The operator-facing error, by contrast, MUST carry the numbers — it is the
// only place they appear.
func TestMismatchErrorCarriesBothVersions(t *testing.T) {
	err := ProtocolVersionMismatchError(WireProtocolVersion+1, 0)
	if err == nil {
		t.Fatal("expected an error")
	}
	if !strings.Contains(err.Error(), "protocol version") {
		t.Fatalf("error %q should name what mismatched", err)
	}

	unv := ProtocolVersionMismatchError(ProtocolVersionUnversioned, 1)
	if !strings.Contains(unv.Error(), "no protocol version") {
		t.Fatalf("unversioned error %q should say the peer advertised nothing", unv)
	}
}

// Version zero is reserved for "did not advertise" and must never be a real
// version, or an old peer and a version-zero peer become indistinguishable —
// the exact trap documented on EntitySnapshot.speed.
func TestVersionNumberingStartsAtOne(t *testing.T) {
	if ProtocolVersionUnversioned != 0 {
		t.Fatalf("the unversioned sentinel must be 0 (proto3 elides it), got %d",
			ProtocolVersionUnversioned)
	}
	if WireProtocolVersion < 1 {
		t.Fatalf("WireProtocolVersion must be >= 1 so it is distinguishable from an absent field, got %d",
			WireProtocolVersion)
	}
}

// The field has to survive both encodings, on both hops, in both directions.
func TestProtocolVersionRoundTripsBothEncodings(t *testing.T) {
	for _, enc := range bothEncodings {
		t.Run(enc.String(), func(t *testing.T) {
			t.Run("AuthRequest", func(t *testing.T) {
				want := AuthRequest{Token: "jwt", ProtocolVersion: WireProtocolVersion}
				var got AuthRequest
				roundTrip(t, enc, MsgAuth, want, &got)
				if got != want {
					t.Fatalf("got %+v, want %+v", got, want)
				}
			})

			t.Run("AuthResponse", func(t *testing.T) {
				want := AuthResponse{
					OK:              false,
					Error:           ReasonProtocolVersionMismatch,
					ProtocolVersion: WireProtocolVersion,
				}
				var got AuthResponse
				roundTrip(t, enc, MsgAuthResp, want, &got)
				if got != want {
					t.Fatalf("got %+v, want %+v", got, want)
				}
			})

			t.Run("JoinTokenRequest", func(t *testing.T) {
				want := JoinTokenRequest{Token: "jt", ProtocolVersion: WireProtocolVersion}
				var got JoinTokenRequest
				roundTrip(t, enc, MsgJoinToken, want, &got)
				if got != want {
					t.Fatalf("got %+v, want %+v", got, want)
				}
			})

			t.Run("JoinTokenResponse", func(t *testing.T) {
				want := JoinTokenResponse{
					OK:              true,
					UserID:          "u-42",
					TickRate:        60,
					ProtocolVersion: WireProtocolVersion,
				}
				var got JoinTokenResponse
				roundTrip(t, enc, MsgJoinTokenResp, want, &got)
				if got != want {
					t.Fatalf("got %+v, want %+v", got, want)
				}
			})
		})
	}
}

// An OLD peer sends no version field at all. Over Protobuf that is
// indistinguishable from an explicit 0 — which is precisely why 0 is reserved
// and why the receiver treats it as "unknown" rather than "version zero".
func TestAbsentVersionDecodesAsUnversioned(t *testing.T) {
	for _, enc := range bothEncodings {
		t.Run(enc.String(), func(t *testing.T) {
			// A peer that predates the field encodes exactly this: the struct
			// without the field set.
			old := JoinTokenRequest{Token: "jt"}

			var got JoinTokenRequest
			roundTrip(t, enc, MsgJoinToken, old, &got)

			if got.ProtocolVersion != ProtocolVersionUnversioned {
				t.Fatalf("an old peer must decode as unversioned, got %d", got.ProtocolVersion)
			}
			if v := CheckProtocolVersion(got.ProtocolVersion, 0); v != VersionAcceptedUnversioned {
				t.Fatalf("default config must admit an old peer, got %v", v)
			}
			if v := CheckProtocolVersion(got.ProtocolVersion, 1); v != VersionRefused {
				t.Fatalf("min=1 must refuse an old peer, got %v", v)
			}
		})
	}
}

// roundTrip encodes payload as msgType in enc and decodes it back into out,
// exercising the same Envelope path a real connection uses.
func roundTrip(t *testing.T, enc Encoding, msgType MsgType, payload any, out any) {
	t.Helper()

	env, err := NewEnvelopeAs(enc, msgType, payload)
	if err != nil {
		t.Fatalf("NewEnvelopeAs: %v", err)
	}

	wire, err := Encode(env)
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}

	decoded, err := DecodeBody(wire[4:])
	if err != nil {
		t.Fatalf("DecodeBody: %v", err)
	}
	if decoded.Type != msgType {
		t.Fatalf("type: got %d want %d", decoded.Type, msgType)
	}
	if err := decoded.UnmarshalPayload(out); err != nil {
		t.Fatalf("UnmarshalPayload: %v", err)
	}
}
