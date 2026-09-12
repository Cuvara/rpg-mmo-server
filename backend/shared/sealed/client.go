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
}

// ClientHandshakeConfig is what the client half needs.
type ClientHandshakeConfig struct {
	// JTI is the join token's jti claim, which anchors the transcript.
	JTI string

	// JoinTokenSecret enables binding verification. EMPTY MEANS DO NOT VERIFY,
	// which is the correct configuration for a shipped client and the wrong one
	// for a test harness that holds the secret anyway.
	JoinTokenSecret string
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
	readHello func() (serverPublic, binding []byte, serverError string, err error),
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

	serverPublicBytes, bindingBytes, serverError, err := readHello()
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

	return ClientResult{Outbound: outbound, Inbound: inbound, BindingVerified: verified}, nil
}
