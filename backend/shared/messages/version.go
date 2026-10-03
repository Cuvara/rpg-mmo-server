package messages

import "fmt"

// MinSupportedProtocolVersion is the oldest wire protocol version this build
// still serves. Mirrors GameServer/Net/WireProtocol.MinSupportedProtocolVersion
// (C#).
//
// Version 3 (ADR-28..31) is a superset a server can withhold: a version 2 peer is
// served the version 2 shape (no z, stats, statuses, projectile or item entities,
// or command channel), byte for byte what a version 2 server sent. So the
// admitted window is [MinSupportedProtocolVersion, WireProtocolVersion]. Raising
// this to 3 retires version 2, which is a production decision made first with
// the receivers' configured minimum (--min-protocol-version /
// GAMESERVER_MIN_PROTOCOL_VERSION).
const MinSupportedProtocolVersion uint32 = 2

// VersionVerdict is the outcome of checking a peer's advertised wire protocol
// version. It distinguishes the two ACCEPT cases, because they need different
// operational treatment: one is a healthy peer and the other is a peer running
// on trust that has to be counted before the trust can be withdrawn.
type VersionVerdict int

const (
	// VersionAccepted: the peer advertised a version inside the supported window
	// [MinSupportedProtocolVersion, WireProtocolVersion], at or above the
	// receiver's configured minimum.
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
// The rule is a WINDOW this build knows how to serve, narrowed from below by the
// configured minimum, with one configured exemption for the unversioned case:
//
//	MinSupported <= peer <= WireProtocolVersion && peer >= minVersion -> VersionAccepted
//	peerVersion == 0 && minVersion == 0                                -> VersionAcceptedUnversioned
//	anything else                                                      -> VersionRefused
//
// A peer AHEAD of this build is still refused just as firmly as one below the
// window: this build cannot know what a later version changed, and admitting it
// would be a guess made at exactly the moment the protocol said not to guess.
// Inside the window every version is one this build implements in full: the
// game server serves each connection the shape of the version it advertised, and
// the gateway (which never carries a snapshot) has nothing version-specific to
// serve at all. The window is a property of this build, not something
// negotiated, so it widens only by a deliberate change to the constant above.
//
// minVersion is a deployment knob, not a protocol constant: 0 admits unversioned
// peers (the shipping default), 1 requires every peer to advertise, and 3
// retires protocol 2 once nothing speaks it.
func CheckProtocolVersion(peerVersion, minVersion uint32) VersionVerdict {
	if peerVersion >= MinSupportedProtocolVersion && peerVersion <= WireProtocolVersion &&
		peerVersion >= minVersion {
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
	return fmt.Errorf("peer speaks protocol version %d, this build serves %d-%d (configured minimum %d)",
		peerVersion, MinSupportedProtocolVersion, WireProtocolVersion, minVersion)
}

// NegotiatedProtocolVersion is the version a server ECHOES on a SUCCESSFUL
// handshake reply: the peer's own version when it lies inside this build's
// window [MinSupportedProtocolVersion, WireProtocolVersion], otherwise this
// build's version.
//
// Echoing the peer's version rather than our own is what lets a server that
// admits protocol 2 actually serve a protocol 2 client: clients built before
// protocol 3 accept only an exact echo, so a server echoing 3 to them would be
// refused by the client it just admitted. It also states the truth: the server
// serves that peer the shape of the version it echoes. A NEW client still
// detects an OLD server, because an old server echoes its own lower version
// (or 0), never the client's. Refusals keep echoing WireProtocolVersion so a
// refused client learns which version to be.
func NegotiatedProtocolVersion(peerVersion uint32) uint32 {
	if peerVersion >= MinSupportedProtocolVersion && peerVersion <= WireProtocolVersion {
		return peerVersion
	}
	return WireProtocolVersion
}
