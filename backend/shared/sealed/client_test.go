package sealed

import (
	"crypto/ed25519"
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
	read func() ([]byte, []byte, []byte, string, error),
	serverKeys *DirectionKeys,
	identity ed25519.PublicKey,
) {
	t.Helper()
	server, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	// The server's per-pod identity (ADR-25). Generated here exactly as the pod
	// generates it at startup: nothing loads it from anywhere.
	identity, identityPriv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	var clientPublic [PublicKeySize]byte
	keys := &DirectionKeys{}

	send = func(pub []byte) error {
		copy(clientPublic[:], pub)
		return nil
	}
	read = func() ([]byte, []byte, []byte, string, error) {
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
			return nil, nil, nil, "", err
		}
		signer, err := NewTranscriptSigner(secret, jti)
		if err != nil {
			return nil, nil, nil, "", err
		}
		binding, err := signer.Sign(transcript)
		if err != nil {
			return nil, nil, nil, "", err
		}
		// Real keys come from the server's true private key against the client's
		// public key, which is what a MITM cannot reproduce.
		secretBytes, err := server.SharedSecret(clientPublic)
		if err != nil {
			return nil, nil, nil, "", err
		}
		*keys, err = DeriveKeys(secretBytes, transcript)
		if err != nil {
			return nil, nil, nil, "", err
		}

		// The identity signature is over the REAL server's transcript too, for the
		// same reason the binding is: an attacker who rewrites the hello cannot
		// produce a signature, only replay the one it read. Here that models the
		// stronger of the two defences — an attacker holding JOIN_TOKEN_SECRET
		// could forge a binding but STILL could not forge this, because the
		// private half exists only inside the pod.
		signature, err := SignIdentity(identityPriv, transcript)
		if err != nil {
			return nil, nil, nil, "", err
		}
		return announced[:], binding[:], signature, "", nil
	}
	return send, read, keys, identity
}

func TestClientHandshakeCompletesAndAgreesWithTheServer(t *testing.T) {
	send, read, serverKeys, _ := serverSide(t, testJTI, testSecret, nil)

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
	send, read, _, _ := serverSide(t, testJTI, testSecret, nil)

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
	send, read, _, _ := serverSide(t, testJTI, testSecret, func(pub *[PublicKeySize]byte) {
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
	send, read, _, _ := serverSide(t, testJTI, testSecret, func(pub *[PublicKeySize]byte) {
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
	read := func() ([]byte, []byte, []byte, string, error) {
		return nil, nil, nil, "encryption required", nil
	}
	if _, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI}, send, read); !errors.Is(err, ErrHandshakeRefused) {
		t.Fatalf("err = %v, want ErrHandshakeRefused", err)
	}
}

func TestClientRefusesAMalformedServerHello(t *testing.T) {
	send := func([]byte) error { return nil }
	for name, r := range map[string]func() ([]byte, []byte, []byte, string, error){
		"short public key": func() ([]byte, []byte, []byte, string, error) {
			return make([]byte, 31), make([]byte, BindingSize), nil, "", nil
		},
		"short binding": func() ([]byte, []byte, []byte, string, error) {
			return make([]byte, PublicKeySize), make([]byte, 31), nil, "", nil
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
	read := func() ([]byte, []byte, []byte, string, error) { return nil, nil, nil, "", nil }
	if _, err := RunClientHandshake(ClientHandshakeConfig{}, send, read); !errors.Is(err, ErrBadJTI) {
		t.Fatalf("err = %v, want ErrBadJTI", err)
	}
}

// ---------------------------------------------------------------- ADR-25

// A shipped client holds no JOIN_TOKEN_SECRET and can never verify the binding,
// but it CAN verify the identity signature, because that needs only a public
// key. This is the state ADR-25 exists to make reachable: binding_verified stays
// false forever and identity is checked anyway.
func TestClientWithoutSecretStillChecksTheIdentitySignature(t *testing.T) {
	send, read, _, identity := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, ServerPublicKey: identity}, send, read)
	if err != nil {
		t.Fatal(err)
	}
	if got.BindingVerified {
		t.Fatal("BindingVerified = true with no join-token secret")
	}
	if !got.IdentityChecked {
		t.Fatal("IdentityChecked = false: a client with the public key must check it, " +
			"which is the whole point of replacing a symmetric binding")
	}
}

// DECISION 6, AND THE REASON THIS CHANGE IS WORTH ANYTHING. Over a plaintext
// gateway hop the client checked a signature against a key an attacker on its
// path could have chosen, so it has learned that its peer holds THAT key and
// nothing about whose key it is. IdentityVerified must stay false.
func TestIdentityVerifiedIsFalseWhenTheKeyArrivedOverAnUnauthenticatedHop(t *testing.T) {
	send, read, _, identity := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(ClientHandshakeConfig{
		JTI:             testJTI,
		ServerPublicKey: identity,
		// Every environment today. ADR-23's TLS is implemented and off.
		KeyHopAuthenticated: false,
	}, send, read)
	if err != nil {
		t.Fatal(err)
	}
	if !got.IdentityChecked {
		t.Fatal("IdentityChecked = false with a good signature")
	}
	if got.IdentityVerified {
		t.Fatal("IdentityVerified = true over a PLAINTEXT gateway hop. An instrument " +
			"that reports the strong claim on the weak evidence is worse than no " +
			"instrument (ADR-25 decision 6)")
	}
	if got.IdentityKeyHopAuthenticated {
		t.Fatal("IdentityKeyHopAuthenticated = true was not what the caller said")
	}
}

// And the other direction: the strong claim IS reachable, with no protocol
// change and no client release, the day ADR-23's gateway TLS is on. A boolean
// that could never be true is the dead end ADR-25 rejected Option D for, so the
// reachability is asserted rather than assumed.
func TestIdentityVerifiedBecomesTrueOverAnAuthenticatedHop(t *testing.T) {
	send, read, _, identity := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(ClientHandshakeConfig{
		JTI:                 testJTI,
		ServerPublicKey:     identity,
		KeyHopAuthenticated: true,
	}, send, read)
	if err != nil {
		t.Fatal(err)
	}
	if !got.IdentityVerified {
		t.Fatal("IdentityVerified = false with a checked signature over an " +
			"authenticated hop: the authenticated state is unreachable, which is " +
			"exactly the always-false boolean this replaces")
	}
}

// The man in the middle, against the identity signature rather than the binding.
// The attacker substitutes its ephemeral key, the transcript changes, and the
// signature it replayed no longer verifies — and unlike the binding, the client
// needed no secret to catch it.
func TestClientRefusesASubstitutedKeyUsingTheIdentitySignatureAlone(t *testing.T) {
	send, read, _, identity := serverSide(t, testJTI, testSecret, func(pub *[PublicKeySize]byte) {
		pub[0] ^= 0xFF
	})

	_, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, ServerPublicKey: identity}, send, read)
	if !errors.Is(err, ErrIdentityNotVerified) {
		t.Fatalf("err = %v, want ErrIdentityNotVerified — a client holding only the "+
			"PUBLIC key must catch a substituted ephemeral key, which is the "+
			"man-in-the-middle defence a shipped player can finally reach", err)
	}
}

// A signature forged by an attacker who runs a pod of his own must be refused
// even though it is a perfectly valid Ed25519 signature. The question is never
// "is this a signature" but "is this OUR signature".
func TestClientRefusesASignatureFromAnotherIdentity(t *testing.T) {
	send, read, _, _ := serverSide(t, testJTI, testSecret, nil)
	attacker, _ := testIdentity(t)

	_, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, ServerPublicKey: attacker}, send, read)
	if !errors.Is(err, ErrIdentityNotVerified) {
		t.Fatalf("err = %v, want ErrIdentityNotVerified", err)
	}
}

// ADR-25 decision 5: no negotiation, no fallback. A client that requires
// identity and is handed a server that sends none is REFUSED — never quietly
// downgraded to the unverified session it would have had before.
func TestClientRequiringIdentityRefusesAServerThatSendsNoSignature(t *testing.T) {
	send, read, _, identity := serverSide(t, testJTI, testSecret, nil)
	stripped := func() ([]byte, []byte, []byte, string, error) {
		pub, binding, _, serr, err := read()
		return pub, binding, nil, serr, err
	}

	_, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, ServerPublicKey: identity}, send, stripped)
	if !errors.Is(err, ErrIdentityMissing) {
		t.Fatalf("err = %v, want ErrIdentityMissing — continuing here would be the "+
			"downgrade ADR-22 decision 3 forbids", err)
	}
}

// The migration order ADR-25 §5 requires: gateway and server first, client
// second. A client given NO key completes against a server that signs, so a pod
// rolling out ahead of the gateway never locks anyone out.
func TestClientWithoutAKeyIgnoresTheSignatureAndStillConnects(t *testing.T) {
	send, read, _, _ := serverSide(t, testJTI, testSecret, nil)

	got, err := RunClientHandshake(ClientHandshakeConfig{JTI: testJTI}, send, read)
	if err != nil {
		t.Fatalf("a client with no identity key was refused: %v", err)
	}
	if got.IdentityChecked || got.IdentityVerified {
		t.Fatal("reported an identity check it did not perform")
	}
	if got.Outbound == nil || got.Inbound == nil {
		t.Fatal("no sessions installed")
	}
}

// The signature is checked BEFORE any session is built, so a refused handshake
// never half-constructs the thing it is refusing.
func TestARefusedIdentityInstallsNoSession(t *testing.T) {
	send, read, _, _ := serverSide(t, testJTI, testSecret, nil)
	attacker, _ := testIdentity(t)

	got, err := RunClientHandshake(
		ClientHandshakeConfig{JTI: testJTI, ServerPublicKey: attacker}, send, read)
	if err == nil {
		t.Fatal("expected a refusal")
	}
	if got.Outbound != nil || got.Inbound != nil {
		t.Fatal("a refused handshake installed a session")
	}
}
