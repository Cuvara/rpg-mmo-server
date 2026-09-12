package sealed

import (
	"errors"
	"testing"
)

// passthroughAEAD is a TEST DOUBLE, not a cipher and not a stub of one. It does
// no encryption whatsoever: Seal appends the plaintext and a fixed 16 zero
// bytes, Open strips them. It exists solely to let the ordering and plumbing in
// Session be tested without a real primitive, and it must never leave this file.
//
// It deliberately cannot be mistaken for a cipher: the "ciphertext" is the
// plaintext, in the clear, and the "tag" is constant.
type passthroughAEAD struct {
	openCalls int
	failOpen  bool
}

func (p *passthroughAEAD) NonceSize() int { return NonceSize }
func (p *passthroughAEAD) Overhead() int  { return TagSize }

func (p *passthroughAEAD) Seal(dst, _, plaintext, _ []byte) []byte {
	dst = append(dst, plaintext...)
	return append(dst, make([]byte, TagSize)...)
}

func (p *passthroughAEAD) Open(dst, _, ciphertext, _ []byte) ([]byte, error) {
	p.openCalls++
	if p.failOpen {
		return nil, errors.New("test double: tag rejected")
	}
	if len(ciphertext) < TagSize {
		return nil, errors.New("test double: short")
	}
	return append(dst, ciphertext[:len(ciphertext)-TagSize]...), nil
}

// countingValidator records whether it was consulted, which is how the ordering
// rule is tested rather than asserted.
type countingValidator struct {
	calls  int
	inner  SequenceValidator
	lastAt int
}

func (c *countingValidator) Accept(seq uint64) error {
	c.calls++
	return c.inner.Accept(seq)
}
func (c *countingValidator) Highest() uint64 { return c.inner.Highest() }
func (c *countingValidator) RequiresOrderedTransport() bool {
	return c.inner.RequiresOrderedTransport()
}

func newTestSession(t *testing.T) (*Session, *passthroughAEAD, *countingValidator) {
	t.Helper()
	aead := &passthroughAEAD{}
	v := &countingValidator{inner: NewStrictMonotonic()}
	s, err := NewSession(aead, v, TransportGuarantees{OrderedDelivery: true})
	if err != nil {
		t.Fatal(err)
	}
	return s, aead, v
}

func TestSessionRoundTrips(t *testing.T) {
	send, _, _ := newTestSession(t)
	recv, _, _ := newTestSession(t)

	for i := 0; i < 4; i++ {
		payload := []byte{0x08, byte(i), 'h', 'i'}
		frame, err := send.Seal(payload)
		if err != nil {
			t.Fatal(err)
		}
		// A sealed frame must be recognisable as one.
		if frame[0] != Marker {
			t.Fatalf("frame does not start with the sealed marker: 0x%02X", frame[0])
		}
		got, err := recv.Open(frame)
		if err != nil {
			t.Fatalf("open: %v", err)
		}
		if string(got) != string(payload) {
			t.Fatalf("round trip: got %x want %x", got, payload)
		}
	}
}

// THE ORDERING RULE, tested structurally rather than trusted to a comment.
//
// A frame whose tag does not verify must never reach the replay validator. If
// it did, an attacker could replay a captured header with garbage after it and
// advance the peer's window without holding any key — locking out the real
// sender at no cost, and presenting as a connectivity bug.
func TestForgedFrameNeverReachesTheReplayWindow(t *testing.T) {
	send, _, _ := newTestSession(t)
	recv, aead, validator := newTestSession(t)

	frame, err := send.Seal([]byte("payload"))
	if err != nil {
		t.Fatal(err)
	}

	aead.failOpen = true
	if _, err := recv.Open(frame); !errors.Is(err, ErrSessionFailed) {
		t.Fatalf("err = %v, want ErrSessionFailed", err)
	}

	if aead.openCalls != 1 {
		t.Errorf("AEAD.Open called %d times, want 1", aead.openCalls)
	}
	if validator.calls != 0 {
		t.Fatalf("the replay validator was consulted %d times for a frame whose tag "+
			"did not verify; the sequence must not be acted on until the AEAD succeeds",
			validator.calls)
	}
	if got := recv.HighestReceived(); got != 0 {
		t.Errorf("a forged frame advanced the window to %d", got)
	}

	// And the real frame must still be accepted afterwards: the forgery must not
	// have consumed its sequence.
	aead.failOpen = false
	if _, err := recv.Open(frame); err != nil {
		t.Fatalf("genuine frame rejected after a forgery attempt: %v", err)
	}
}

// A replay that DOES authenticate must be refused, and must look identical to a
// forgery from the outside.
func TestAuthenticatedReplayIsRefusedIndistinguishably(t *testing.T) {
	send, _, _ := newTestSession(t)
	recv, _, _ := newTestSession(t)

	frame, err := send.Seal([]byte("payload"))
	if err != nil {
		t.Fatal(err)
	}
	// Copy: Seal reuses its buffer, so the second Open must not read a mutated one.
	captured := append([]byte(nil), frame...)

	if _, err := recv.Open(captured); err != nil {
		t.Fatal(err)
	}
	err = func() error { _, e := recv.Open(captured); return e }()
	if !errors.Is(err, ErrSessionFailed) {
		t.Fatalf("replayed frame: err = %v, want ErrSessionFailed", err)
	}
}

// The send counter must advance even if a frame is never transmitted. A nonce
// reused after a failed send is the same catastrophe as one reused on purpose.
func TestSendSequenceNeverRepeats(t *testing.T) {
	s, _, _ := newTestSession(t)

	seen := map[uint64]bool{}
	for i := 0; i < 100; i++ {
		before := s.NextSendSequence()
		if seen[before] {
			t.Fatalf("sequence %d issued twice", before)
		}
		seen[before] = true
		if _, err := s.Seal([]byte("x")); err != nil {
			t.Fatal(err)
		}
		if s.NextSendSequence() != before+1 {
			t.Fatalf("counter did not advance past %d", before)
		}
	}
}

// A cleartext body must be reported as "not sealed" rather than as a failure,
// because the caller distinguishes them: during a handshake it is expected, and
// afterwards it is a peer sending cleartext where a sealed frame is required.
func TestCleartextBodyIsDistinguishable(t *testing.T) {
	recv, _, _ := newTestSession(t)
	if _, err := recv.Open([]byte{0x08, 0x01, 0x02}); !errors.Is(err, ErrNotSealed) {
		t.Fatalf("err = %v, want ErrNotSealed", err)
	}
}

// An AEAD whose geometry does not match the frame layout must be refused at
// construction, not discovered at the first frame.
func TestSessionRefusesAMismatchedAEAD(t *testing.T) {
	if _, err := NewSession(nil, NewStrictMonotonic(), TransportGuarantees{OrderedDelivery: true}); err == nil {
		t.Error("nil AEAD accepted")
	}
	if _, err := NewSession(&passthroughAEAD{}, nil, TransportGuarantees{OrderedDelivery: true}); err == nil {
		t.Error("nil validator accepted")
	}
	if _, err := NewSession(&wrongGeometryAEAD{}, NewStrictMonotonic(), TransportGuarantees{OrderedDelivery: true}); err == nil {
		t.Error("an AEAD with the wrong nonce size was accepted")
	}
}

type wrongGeometryAEAD struct{ passthroughAEAD }

func (w *wrongGeometryAEAD) NonceSize() int { return 8 }

// The ordering guarantee this protocol rests on is INHERITED, not owned: TCP
// gives it by definition and KCP gets it from a hand-ported reassembly path. A
// future transport that does not promise it must fail closed at construction,
// not degrade into dropping legitimate frames and presenting as packet loss.
func TestSessionRefusesAnUnorderedTransport(t *testing.T) {
	_, err := NewSession(&passthroughAEAD{}, NewStrictMonotonic(),
		TransportGuarantees{OrderedDelivery: false})
	if !errors.Is(err, ErrUnorderedTransport) {
		t.Fatalf("err = %v, want ErrUnorderedTransport — a strict counter over an "+
			"unordered transport silently drops legitimate frames", err)
	}

	// A window tolerates reordering, so it is allowed there.
	if _, err := NewSession(&passthroughAEAD{}, NewSlidingWindow(),
		TransportGuarantees{OrderedDelivery: false}); err != nil {
		t.Errorf("a sliding window was refused an unordered transport: %v", err)
	}
}

// A validator that refuses silently is indistinguishable from one that was
// never wired in — the same lesson as the counter-priming bug, one layer down.
// Under attack these counters are the only thing in the system that changes.
func TestRejectionsAreCounted(t *testing.T) {
	send, _, _ := newTestSession(t)
	recv, aead, _ := newTestSession(t)

	frame, err := send.Seal([]byte("payload"))
	if err != nil {
		t.Fatal(err)
	}
	captured := append([]byte(nil), frame...)

	// 1. A frame that does not authenticate.
	aead.failOpen = true
	if _, err := recv.Open(captured); !errors.Is(err, ErrSessionFailed) {
		t.Fatal(err)
	}
	aead.failOpen = false

	// 2. An authentic frame, then the same one replayed.
	if _, err := recv.Open(captured); err != nil {
		t.Fatal(err)
	}
	if _, err := recv.Open(captured); !errors.Is(err, ErrSessionFailed) {
		t.Fatal(err)
	}

	// 3. An authentic frame that leaps past the bound.
	jumped, err := (&Session{aead: &passthroughAEAD{}, sendSeq: MaxForwardJump + 99}).Seal([]byte("x"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := recv.Open(jumped); !errors.Is(err, ErrSessionFailed) {
		t.Fatal(err)
	}

	got := recv.Rejections()
	if got.NotAuthenticated != 1 {
		t.Errorf("NotAuthenticated = %d, want 1", got.NotAuthenticated)
	}
	if got.Replayed != 1 {
		t.Errorf("Replayed = %d, want 1", got.Replayed)
	}
	if got.ForwardJump != 1 {
		t.Errorf("ForwardJump = %d, want 1", got.ForwardJump)
	}
}
