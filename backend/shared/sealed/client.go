package sealed

import (
	"errors"
	"fmt"
)

// Errors returned by the client half of the handshake.
var (
	// ErrHandshakeRefused reports that the server declined the exchange.
	ErrHandshakeRefused = errors.New("sealed: server refused the handshake")
	// ErrBadServerHello reports a malformed or missing server hello.
	ErrBadServerHello = errors.New("sealed: malformed server hello")
)

// ClientResult is what a completed client handshake produced.
type ClientResult struct {
	// Outbound seals frames this peer sends.
	Outbound *Session
	// Inbound opens frames this peer receives.
	Inbound *Session

	// BindingVerified reports whether the server's binding was checked.
	//
	// FALSE IS NOT A FAILURE, and it is not a detail. Verifying the binding needs
	// material derived from JOIN_TOKEN_SECRET, and a shipped game client must not
	// carry that secret — putting it in a binary is exactly the pre-shared-key
	// mistake ADR-22 supersedes. So a real client runs with this false and gets
	// confidentiality against a PASSIVE eavesdropper, which the X25519 exchange
	// provides on its own, and NOTHING against an active one: nothing it holds
	// can tell the real server's ephemeral key from an attacker's.
	//
	// A server-side harness that already holds the secret — the load generator,
	// the smoke test, the integration suite — SHOULD verify, and then this is
	// true. Those are the only peers that can currently prove the
	// man-in-the-middle defence works end to end.
	BindingVerified bool

	// IdentityChecked reports that the server's Ed25519 signature was verified
	// against the identity key this client was given (ADR-25).
	//
	// TRUE HERE IS NOT AN IDENTITY GUARANTEE ON ITS OWN, and the split between
	// this field and IdentityVerified is the whole point. What a true here
	// proves is that the peer on the gameplay hop holds the private half of the
	// key named in EnterWorldResponse.server_public_key. Whether that key is the
	// real server's is a question about the hop that DELIVERED it.
	IdentityChecked bool

	// IdentityKeyHopAuthenticated echoes back what the caller asserted about the
	// hop the identity key arrived over — ADR-23 gateway TLS in force with the
	// certificate validated, or not.
	//
	// It is carried on the result rather than left in the caller's config so
	// that a reporting path which only sees a ClientResult cannot report the
	// strong claim without also being able to see the evidence for it.
	IdentityKeyHopAuthenticated bool

	// IdentityVerified is the ONLY field that means "this is the real game
	// server": IdentityChecked AND IdentityKeyHopAuthenticated (ADR-25
	// decision 6).
	//
	// Over a plaintext gateway hop — every environment today — the client has
	// checked a signature against a key an attacker on its network path could
	// have chosen, and this stays FALSE while IdentityChecked is true. That is
	// the weaker truth, and reporting it is not pessimism: an attacker who can
	// man-in-the-middle the gameplay hop is on the same path as the gateway hop,
	// substitutes the key in enter_world_resp, and the signature he then forges
	// verifies perfectly. An instrument that reported success there would be
	// worse than no instrument, which is ADR-23's argument against its own
	// Option A, applied to ourselves.
	//
	// It becomes reachable — with no protocol change and no client release — the
	// day ADR-23's gateway TLS is on and the caller can honestly pass
	// KeyHopAuthenticated: true.
	IdentityVerified bool
}

// ClientHandshakeConfig is what the client half needs.
type ClientHandshakeConfig struct {
	// JTI is the join token's jti claim, which anchors the transcript.
	JTI string

	// JoinTokenSecret enables binding verification. EMPTY MEANS DO NOT VERIFY,
	// which is the correct configuration for a shipped client and the wrong one
	// for a test harness that holds the secret anyway.
	JoinTokenSecret string

	// ServerPublicKey is the game server's Ed25519 identity key, 32 bytes, as
	// delivered in EnterWorldResponse.server_public_key (ADR-25).
	//
	// NON-EMPTY MEANS REQUIRE IDENTITY. Unlike JoinTokenSecret, which a shipped
	// client can never hold, this is the field a real player's client SHOULD
	// set: it carries no secret, so setting it costs nothing and refusing
	// without it costs a session that cannot be authenticated. When it is set,
	// a missing, malformed or non-verifying signature ends the handshake with an
	// error and no session — there is no negotiation and no fallback (ADR-22
	// decision 3, ADR-25 decision 5).
	//
	// Empty means the gateway had no key for this server (an older pod, or an
	// entry written before the field existed) or the caller chose not to
	// require one. Then the handshake proceeds exactly as it did before ADR-25
	// and IdentityChecked is false.
	ServerPublicKey []byte

	// KeyHopAuthenticated asserts that ServerPublicKey arrived over an
	// AUTHENTICATED hop — ADR-23 gateway TLS in force, certificate validated —
	// and it is what separates IdentityChecked from IdentityVerified on the
	// result.
	//
	// DEFAULT FALSE IS THE HONEST DEFAULT AND MUST STAY THAT WAY. Every
	// environment runs the gateway hop in plaintext today, so a caller that sets
	// this without actually having validated a certificate is not configuring a
	// feature; it is falsifying the one field anyone would trust. Set it from
	// what the transport actually did, never from what the deployment intends.
	KeyHopAuthenticated bool
}

// RunClientHandshake performs the client half of the sealed-session exchange.
//
// It mirrors the server half (GameServer.Net.Sealed.SealedHandshakeServer) step
// for step, and contains no transport: the caller supplies sendHello and
// readHello so the same logic serves the load generator, the smoke test and the
// integration suite, which each frame bytes differently.
//
// # There is no fallback on any path
//
// Every failure returns an error and no session. A caller that received an error
// must close the connection, never continue in cleartext — a protocol that can
// be talked down to cleartext will be, and the only reliable defence is to have
// nothing to downgrade to.
func RunClientHandshake(
	cfg ClientHandshakeConfig,
	sendHello func(publicKey []byte) error,
	readHello func() (serverPublic, binding, serverSignature []byte, serverError string, err error),
) (ClientResult, error) {
	if cfg.JTI == "" {
		return ClientResult{}, ErrBadJTI
	}

	kp, err := GenerateKeyPair()
	if err != nil {
		return ClientResult{}, fmt.Errorf("sealed: client key: %w", err)
	}
	if err := sendHello(kp.Public[:]); err != nil {
		return ClientResult{}, fmt.Errorf("sealed: send client hello: %w", err)
	}

	serverPublicBytes, bindingBytes, signatureBytes, serverError, err := readHello()
	if err != nil {
		return ClientResult{}, fmt.Errorf("sealed: read server hello: %w", err)
	}
	if serverError != "" {
		return ClientResult{}, fmt.Errorf("%w: %s", ErrHandshakeRefused, serverError)
	}
	if len(serverPublicBytes) != PublicKeySize {
		return ClientResult{}, fmt.Errorf("%w: public key is %d bytes, want %d",
			ErrBadServerHello, len(serverPublicBytes), PublicKeySize)
	}

	var serverPublic [PublicKeySize]byte
	copy(serverPublic[:], serverPublicBytes)

	transcript, err := Transcript(cfg.JTI, kp.Public, serverPublic)
	if err != nil {
		return ClientResult{}, err
	}

	verified := false
	if cfg.JoinTokenSecret != "" {
		if len(bindingBytes) != BindingSize {
			return ClientResult{}, fmt.Errorf("%w: binding is %d bytes, want %d",
				ErrBadServerHello, len(bindingBytes), BindingSize)
		}
		signer, err := NewTranscriptSigner(cfg.JoinTokenSecret, cfg.JTI)
		if err != nil {
			return ClientResult{}, err
		}
		var binding [BindingSize]byte
		copy(binding[:], bindingBytes)
		// A failure here is a MAN IN THE MIDDLE, not a configuration slip: the
		// transcript covers both ephemeral public keys, so a substituted key is
		// exactly what makes this fail. Refuse the session.
		if err := signer.Verify(transcript, binding); err != nil {
			return ClientResult{}, err
		}
		verified = true
	}

	// ADR-25. Checked BEFORE the shared secret is derived and before any session
	// exists, for the same reason the binding is: a handshake that is going to be
	// refused must not first build the thing it would have installed.
	identityChecked := false
	if len(cfg.ServerPublicKey) > 0 {
		if len(signatureBytes) == 0 {
			// The caller required identity and the peer offered none. Refuse —
			// do not continue as if identity had not been asked for. This is the
			// exact shape ADR-25 decision 5 names: "a client that requires
			// identity and is not given a key is refused".
			return ClientResult{}, ErrIdentityMissing
		}
		if err := VerifyIdentity(cfg.ServerPublicKey, transcript, signatureBytes); err != nil {
			return ClientResult{}, err
		}
		identityChecked = true
	}

	secret, err := kp.SharedSecret(serverPublic)
	if err != nil {
		return ClientResult{}, err
	}
	keys, err := DeriveKeys(secret, transcript)
	if err != nil {
		return ClientResult{}, err
	}

	// The client SENDS on the client-to-server key and RECEIVES on the other.
	// Getting this pair the wrong way round is the one mistake here that still
	// looks like a working handshake and then fails on the first frame, so it is
	// worth naming: the direction names are from the SERVER's point of view,
	// because that is whose schedule the labels were written for.
	outAead, err := NewAEAD(keys.ClientToServer[:])
	if err != nil {
		return ClientResult{}, err
	}
	inAead, err := NewAEAD(keys.ServerToClient[:])
	if err != nil {
		return ClientResult{}, err
	}

	ordered := TransportGuarantees{OrderedDelivery: true}
	outbound, err := NewSession(outAead, NewStrictMonotonic(), ordered)
	if err != nil {
		return ClientResult{}, err
	}
	inbound, err := NewSession(inAead, NewStrictMonotonic(), ordered)
	if err != nil {
		return ClientResult{}, err
	}

	return ClientResult{
		Outbound:        outbound,
		Inbound:         inbound,
		BindingVerified: verified,
		IdentityChecked: identityChecked,
		// The conjunction is computed HERE, once, rather than left to each
		// reporting site to remember. Three call sites each re-deriving "checked
		// and authenticated" is three chances for one of them to report the
		// strong claim on the weak evidence.
		IdentityKeyHopAuthenticated: cfg.KeyHopAuthenticated,
		IdentityVerified:            identityChecked && cfg.KeyHopAuthenticated,
	}, nil
}
