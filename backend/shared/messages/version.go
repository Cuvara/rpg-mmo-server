package messages

import "fmt"

// VersionVerdict is the outcome of checking a peer's advertised wire protocol
// version. It distinguishes the two ACCEPT cases, because they need different
// operational treatment: one is a healthy peer and the other is a peer running
// on trust that has to be counted before the trust can be withdrawn.
type VersionVerdict int

const (
	// VersionAccepted: the peer advertised exactly this build's version.
	VersionAccepted VersionVerdict = iota

	// VersionAcceptedUnversioned: the peer advertised nothing (0) and the
	// receiver's configured minimum still tolerates that.
	//
	// This is admission on trust, not on evidence — the peer may be a build from
	// before the field existed, or a non-conforming client that simply omits it,
	// and neither is distinguishable from the other. Callers MUST count this
	// separately (an `*_unversioned_handshakes_total` counter) rather than
	// folding it into the accept path: the counter reaching zero across a fleet
	// is the only signal that raising the minimum to 1 is safe, and an admission
	// nobody can see is exactly the silent fallback the tick_rate rule in
	// gameserver-dotnet/docs/API.md forbids.
	VersionAcceptedUnversioned

	// VersionRefused: the peer's version is one this build cannot serve.
	VersionRefused
)

// CheckProtocolVersion decides whether a peer advertising peerVersion may be
// admitted by a receiver whose configured floor is minVersion.
//
// The rule is EXACT MATCH against WireProtocolVersion, with one configured
// exemption for the unversioned case:
//
//	peerVersion == WireProtocolVersion            -> VersionAccepted
//	peerVersion == 0 && minVersion == 0           -> VersionAcceptedUnversioned
//	anything else                                 -> VersionRefused
//
// Exact match, rather than "peer >= minVersion", is the honest rule for a single
// integer carrying no compatibility range. A peer one version AHEAD is refused
// just as firmly as one behind: this build cannot know what a later version
// changed, and admitting it would be a guess made at exactly the moment the
// protocol said not to guess. When a genuine compatibility window is wanted, it
// has to be expressed as a range on the wire (a min/max pair) and negotiated —
// which is a schema change, and a deliberate one, not an accident of a >=.
//
// minVersion is a deployment knob, not a protocol constant. It has exactly two
// useful settings today: 0 (admit unversioned peers, the shipping default) and 1
// (require every peer to advertise). Flipping it to 1 once the fleet advertises
// is the migration, and the unversioned counter is what says the flip is safe.
func CheckProtocolVersion(peerVersion, minVersion uint32) VersionVerdict {
	if peerVersion == WireProtocolVersion {
		return VersionAccepted
	}
	if peerVersion == ProtocolVersionUnversioned && minVersion == ProtocolVersionUnversioned {
		return VersionAcceptedUnversioned
	}
	return VersionRefused
}

// ProtocolVersionMismatchError renders the operator-facing detail behind a
// refusal.
//
// This is for logs and metrics ONLY. What goes on the wire is the bare machine
// readable ReasonProtocolVersionMismatch, following the convention set by
// "duplicate_login" and "server_shutdown": a client branches on the reason, and
// a reason that embeds numbers is one a client parses with a regex or, more
// likely, not at all. The numbers are what the operator needs; the token is what
// the client needs.
func ProtocolVersionMismatchError(peerVersion, minVersion uint32) error {
	if peerVersion == ProtocolVersionUnversioned {
		return fmt.Errorf("peer advertised no protocol version and the configured minimum is %d (this build speaks %d)",
			minVersion, WireProtocolVersion)
	}
	return fmt.Errorf("peer speaks protocol version %d, this build speaks %d",
		peerVersion, WireProtocolVersion)
}
