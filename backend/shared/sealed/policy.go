package sealed

import "errors"

// Requirement is how strictly a listener demands a sealed session.
type Requirement int

const (
	// Disabled accepts cleartext and does not offer sealing. The state the
	// server ships in today, and the reason the posture reporting exists.
	Disabled Requirement = iota

	// Required refuses any session that does not complete the sealed handshake.
	// There is deliberately no middle value. See RefusalFor.
	Required
)

// ErrEncryptionRequired reports a peer refused for not sealing its session.
var ErrEncryptionRequired = errors.New("sealed: encryption is required on this listener")

// Refusal says whether a peer may proceed, and why not.
type Refusal struct {
	// Refused reports that the session must be closed.
	Refused bool
	// Reason is a short, stable token for metrics and logs. It never contains
	// anything peer-supplied.
	Reason string
}

// Reasons a peer is refused. Stable strings, because they become metric label
// values and a renamed label silently breaks a dashboard.
const (
	ReasonNone            = ""
	ReasonNoSealedSession = "no_sealed_session"
	ReasonEncodingCannot  = "encoding_cannot_seal"
)

// PeerCapabilities is what the listener knows about a peer at the moment it has
// to decide.
type PeerCapabilities struct {
	// SealedHandshakeCompleted reports that the peer completed the exchange and
	// a verified key is in force.
	SealedHandshakeCompleted bool

	// EncodingCanSeal reports that the peer's wire encoding is able to carry a
	// sealed session at all.
	//
	// The legacy JSON encoding cannot: the sealed frame is a binary layout with
	// a marker byte that JSON has no room for, and the handshake fields were
	// deliberately kept out of the JSON message set so that key material could
	// never be rendered into a human-readable payload. A JSON client therefore
	// cannot be encrypted — which means that once encryption is required, JSON
	// is not merely legacy, it is REFUSED. That effectively deprecates the JSON
	// encoding for any deployment that requires encryption, and that is a
	// consequence to state out loud rather than discover.
	EncodingCanSeal bool
}

// RefusalFor decides whether to admit a peer.
//
// # There is no negotiation, and that is the point
//
// A protocol that can be talked down to cleartext will be: an attacker who can
// modify the handshake simply removes the offer, both ends believe the other
// could not do better, and the session proceeds in the clear looking entirely
// healthy. That is a downgrade attack, and the only reliable defence is to have
// nothing to downgrade TO. So the answer to "the peer did not seal" is no
// session — never a cleartext session, never a weaker cipher, never a retry
// without encryption.
//
// The same reasoning is why Requirement has two values and not three: a
// "preferred" mode is a downgrade attack with a friendly name.
func RefusalFor(req Requirement, peer PeerCapabilities) Refusal {
	if req != Required {
		return Refusal{}
	}
	if !peer.EncodingCanSeal {
		return Refusal{Refused: true, Reason: ReasonEncodingCannot}
	}
	if !peer.SealedHandshakeCompleted {
		return Refusal{Refused: true, Reason: ReasonNoSealedSession}
	}
	return Refusal{}
}
