package sealed

import (
	"errors"
	"testing"
)

const (
	testJTI    = "client-test-jti"
	testSecret = "client-test-join-secret"
)

// serverSide plays the server half well enough to test the client half: it is
// the same sequence GameServer.Net.Sealed.SealedHandshakeServer performs.
func serverSide(t *testing.T, jti, secret string, tamper func(pub *[PublicKeySize]byte)) (
	send func([]byte) error,
	read func() ([]byte, []byte, string, error),
	serverKeys *DirectionKeys,
) {
	t.Helper()
	server, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	var clientPublic [PublicKeySize]byte
	keys := &DirectionKeys{}

	send = func(pub []byte) error {
		copy(clientPublic[:], pub)
		return nil
	}
	read = func() ([]byte, []byte, string, error) {
		announced := server.Public
		if tamper != nil {
			tamper(&announced)
		}

		// THE BINDING IS SIGNED OVER THE REAL SERVER'S KEY, ALWAYS — even when the
		// announced key is tampered with. That is what models the actual attacker:
		// one who can rewrite the hello on the wire but CANNOT compute a binding,
		// because it does not hold JOIN_TOKEN_SECRET. All it can do is replay the
		// binding the real server produced, which no longer matches the transcript
		// the client will build.
		//
		// Signing over the tampered key instead would model an attacker who holds
		// the secret — and against that one there is nothing to defend, since it
		// could simply mint its own join tokens.
		transcript, err := Transcript(jti, clientPublic, server.Public)
		if err != nil {
			return nil, nil, "", err
		}
		signer, err := NewTranscriptSigner(secret, jti)
		if err != nil {
			return nil, nil, "", err
		}
		binding, err := signer.Sign(transcript)
		if err != nil {
			return nil, nil, "", err
		}
		// Real keys come from the server's true private key against the client's
		// public key, which is what a MITM cannot reproduce.
		secretBytes, err := server.SharedSecret(clientPublic)
		if err != nil {
			return nil, nil, "", err
		}
		*keys, err = DeriveKeys(secretBytes, transcript)
		if err != nil {
			return nil, nil, "", err
		}
		return announced[:], binding[:], "", nil
	}
	return send, read, keys
}

func TestClientHandshakeCompletesAndAgreesWithTheServer(t *testing.T) {
	send, read, serverKeys := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, JoinTokenSecret: testSecret}, send, read)
	if err != nil {
		t.Fatal(err)
	}
	if !got.BindingVerified {
		t.Error("BindingVerified = false with a secret supplied")
	}

	// The frames the client seals must open under the key the SERVER derived,
	// which is the property that matters and the one a direction mix-up breaks.
	serverInbound, err := NewAEAD(serverKeys.ClientToServer[:])
	if err != nil {
		t.Fatal(err)
	}
	serverSession, err := NewSession(serverInbound, NewStrictMonotonic(),
		TransportGuarantees{OrderedDelivery: true})
	if err != nil {
		t.Fatal(err)
	}

	frame, err := got.Outbound.Seal([]byte("hello from the client"))
	if err != nil {
		t.Fatal(err)
	}
	plain, err := serverSession.Open(frame)
	if err != nil {
		t.Fatalf("the server could not open a frame the client sealed: %v", err)
	}
	if string(plain) != "hello from the client" {
		t.Fatalf("plaintext = %q", plain)
	}
}

// A shipped client passes no secret. It still completes, still gets a session,
// and REPORTS that it verified nothing — that flag is the whole difference
// between confidentiality and authenticity here, and hiding it would be the
// "believed protected" failure in a boolean.
func TestClientWithoutSecretCompletesButReportsUnverified(t *testing.T) {
	send, read, _ := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(ClientHandshakeConfig{JTI: testJTI}, send, read)
	if err != nil {
		t.Fatal(err)
	}
	if got.BindingVerified {
		t.Fatal("BindingVerified = true with no secret supplied")
	}
	if got.Outbound == nil || got.Inbound == nil {
		t.Fatal("no sessions installed")
	}
}

// THE MAN IN THE MIDDLE. An attacker substitutes its own ephemeral key; the
// transcript changes, so the binding the attacker replayed no longer verifies.
// A verifying client must refuse.
func TestClientWithSecretRefusesASubstitutedKey(t *testing.T) {
	send, read, _ := serverSide(t, testJTI, testSecret, func(pub *[PublicKeySize]byte) {
		pub[0] ^= 0xFF // an attacker's key in place of the server's
	})

	_, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, JoinTokenSecret: testSecret}, send, read)
	if !errors.Is(err, ErrBadBindingTag) {
		t.Fatalf("err = %v, want ErrBadBindingTag — a substituted ephemeral key must be "+
			"caught by the binding, which is the entire man-in-the-middle defence", err)
	}
}

// And the same substitution against a client with NO secret succeeds, which is
// exactly the exposure a shipped client carries today. Asserted rather than
// described, so it starts failing the day the pinned identity key removes it.
func TestClientWithoutSecretIsVulnerableToASubstitutedKey(t *testing.T) {
	send, read, _ := serverSide(t, testJTI, testSecret, func(pub *[PublicKeySize]byte) {
		pub[0] ^= 0xFF
	})

	got, err := RunClientHandshake(ClientHandshakeConfig{JTI: testJTI}, send, read)
	if err != nil {
		t.Fatalf("unexpectedly refused: %v", err)
	}
	if got.BindingVerified {
		t.Fatal("BindingVerified = true without a secret")
	}
	t.Log("a client with no join-token secret completed a handshake against a substituted " +
		"key: confidentiality against a passive listener, nothing against an active one")
}

func TestClientRefusesAServerError(t *testing.T) {
	send := func([]byte) error { return nil }
	read := func() ([]byte, []byte, string, error) {
		return nil, nil, "encryption required", nil
	}
	if _, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI}, send, read); !errors.Is(err, ErrHandshakeRefused) {
		t.Fatalf("err = %v, want ErrHandshakeRefused", err)
	}
}

func TestClientRefusesAMalformedServerHello(t *testing.T) {
	send := func([]byte) error { return nil }
	for name, r := range map[string]func() ([]byte, []byte, string, error){
		"short public key": func() ([]byte, []byte, string, error) {
			return make([]byte, 31), make([]byte, BindingSize), "", nil
		},
		"short binding": func() ([]byte, []byte, string, error) {
			return make([]byte, PublicKeySize), make([]byte, 31), "", nil
		},
	} {
		_, err := RunClientHandshake(
			ClientHandshakeConfig{JTI: testJTI, JoinTokenSecret: testSecret}, send, r)
		if !errors.Is(err, ErrBadServerHello) {
			t.Errorf("%s: err = %v, want ErrBadServerHello", name, err)
		}
	}
}

func TestClientNeedsAJTI(t *testing.T) {
	send := func([]byte) error { return nil }
	read := func() ([]byte, []byte, string, error) { return nil, nil, "", nil }
	if _, err := RunClientHandshake(ClientHandshakeConfig{}, send, read); !errors.Is(err, ErrBadJTI) {
		t.Fatalf("err = %v, want ErrBadJTI", err)
	}
}
