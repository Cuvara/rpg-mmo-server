package sealed

import (
	"bytes"
	"encoding/hex"
	"testing"
)

// The marker must not collide with what the codec sniffs to pick an encoding.
// If it ever does, a sealed frame is parsed as a cleartext Envelope (or a JSON
// object) and the failure surfaces as a malformed message rather than as an
// encryption problem — the most expensive kind of wrong answer.
func TestMarkerCannotBeConfusedWithAnEncoding(t *testing.T) {
	const protobufFirstByte = 0x08 // Envelope field 1, `type`; type 0 is refused
	const jsonFirstByte = 0x7B     // '{'

	if Marker == protobufFirstByte || Marker == jsonFirstByte {
		t.Fatalf("Marker 0x%02X collides with an existing encoding sniff", Marker)
	}
	if _, _, err := ParseHeader([]byte{protobufFirstByte, 0, 0}); err != ErrNotSealed {
		t.Errorf("a protobuf body parsed as sealed: %v", err)
	}
	if _, _, err := ParseHeader([]byte{jsonFirstByte, 0, 0}); err != ErrNotSealed {
		t.Errorf("a JSON body parsed as sealed: %v", err)
	}
}

func TestHeaderRoundTrips(t *testing.T) {
	for _, seq := range []uint64{0, 1, 255, 256, 1 << 32, ^uint64(0)} {
		body := AppendHeader(nil, Header{Version: Version, Sequence: seq})
		body = append(body, make([]byte, TagSize)...) // minimum viable frame

		got, rest, err := ParseHeader(body)
		if err != nil {
			t.Fatalf("seq %d: %v", seq, err)
		}
		if got.Sequence != seq {
			t.Errorf("sequence = %d, want %d", got.Sequence, seq)
		}
		if got.Version != Version {
			t.Errorf("version = %d, want %d", got.Version, Version)
		}
		if len(rest) != TagSize {
			t.Errorf("rest = %d bytes, want %d", len(rest), TagSize)
		}
	}
}

// A future version must be refused, not guessed at. The version byte is inside
// the authenticated data, so this is a compatibility check and not a defence —
// but reading a v2 frame as v1 would misplace every field.
func TestFutureVersionIsRefused(t *testing.T) {
	body := AppendHeader(nil, Header{Version: Version + 1, Sequence: 1})
	body = append(body, make([]byte, TagSize)...)
	if _, _, err := ParseHeader(body); err == nil {
		t.Fatal("a future sealed-frame version was accepted")
	}
}

func TestShortFrameIsRefused(t *testing.T) {
	body := AppendHeader(nil, Header{Version: Version, Sequence: 1})
	if _, _, err := ParseHeader(body); err != ErrShortFrame {
		t.Errorf("err = %v, want ErrShortFrame — a frame with no room for a tag "+
			"cannot be authenticated and must not be attempted", err)
	}
}

// The nonce must be a pure function of the sequence, and distinct for distinct
// sequences. Reusing a (key, nonce) pair with either candidate AEAD leaks the
// XOR of two plaintexts and can expose the Poly1305 key.
func TestNonceIsUniquePerSequence(t *testing.T) {
	seen := map[[NonceSize]byte]uint64{}
	for _, seq := range []uint64{0, 1, 2, 1 << 8, 1 << 16, 1 << 40, ^uint64(0)} {
		n := Nonce(seq)
		if prev, dup := seen[n]; dup {
			t.Fatalf("sequences %d and %d produced the same nonce", prev, seq)
		}
		seen[n] = seq
	}
	if got := Nonce(1); got[NonceSize-1] != 1 {
		t.Errorf("sequence is not in the low bytes of the nonce: %x", got)
	}
	// The first four bytes are reserved for a future direction or rekey epoch.
	if n := Nonce(^uint64(0)); !bytes.Equal(n[:4], []byte{0, 0, 0, 0}) {
		t.Errorf("nonce prefix = %x, want four zero bytes", n[:4])
	}
}

// --- replay ---

func TestStrictMonotonicRejectsAnythingNotIncreasing(t *testing.T) {
	v := NewStrictMonotonic()
	for _, seq := range []uint64{1, 2, 3, 10} {
		if err := v.Accept(seq); err != nil {
			t.Fatalf("seq %d rejected: %v", seq, err)
		}
	}
	for _, seq := range []uint64{10, 9, 1, 0} {
		if err := v.Accept(seq); err != ErrReplay {
			t.Errorf("seq %d after 10: err = %v, want ErrReplay", seq, err)
		}
	}
	if v.Highest() != 10 {
		t.Errorf("Highest = %d, want 10", v.Highest())
	}
}

// Sequence 0 must be usable: counters start there, and a validator that
// silently rejected the first frame would look like a key mismatch.
func TestValidatorsAcceptSequenceZeroFirst(t *testing.T) {
	for name, v := range map[string]SequenceValidator{
		"strict": NewStrictMonotonic(),
		"window": NewSlidingWindow(),
	} {
		if err := v.Accept(0); err != nil {
			t.Errorf("%s: first frame at sequence 0 rejected: %v", name, err)
		}
		if err := v.Accept(0); err != ErrReplay {
			t.Errorf("%s: replay of sequence 0 accepted", name)
		}
	}
}

func TestSlidingWindowAcceptsReorderingButNotReplay(t *testing.T) {
	v := NewSlidingWindow()

	// Arrive out of order, as a reordering transport would deliver them.
	for _, seq := range []uint64{5, 3, 4, 8, 6} {
		if err := v.Accept(seq); err != nil {
			t.Fatalf("seq %d rejected: %v", seq, err)
		}
	}
	// Every one of them is now a replay.
	for _, seq := range []uint64{3, 4, 5, 6, 8} {
		if err := v.Accept(seq); err != ErrReplay {
			t.Errorf("replay of %d: err = %v, want ErrReplay", seq, err)
		}
	}
	// A gap left behind is still acceptable, which is the whole point.
	if err := v.Accept(7); err != nil {
		t.Errorf("late but unseen seq 7 rejected: %v", err)
	}
}

// A frame older than the window cannot be proved fresh, so it must be refused.
// A validator that accepted what it cannot judge is not a replay defence.
func TestSlidingWindowRefusesWhatItCannotJudge(t *testing.T) {
	v := NewSlidingWindowOf(8)
	if err := v.Accept(100); err != nil {
		t.Fatal(err)
	}
	if err := v.Accept(100 - 8); err != ErrReplay {
		t.Errorf("a frame exactly the window width behind was accepted: %v", err)
	}
	if err := v.Accept(1); err != ErrReplay {
		t.Errorf("an ancient frame was accepted: %v", err)
	}
	if err := v.Accept(99); err != nil {
		t.Errorf("a frame inside the window was rejected: %v", err)
	}
}

// A jump forward WITHIN the bound must clear the window rather than shift
// stale bits into it — otherwise frames far behind the new high water read as
// already seen and legitimate traffic is dropped.
func TestSlidingWindowHandlesJumpsInsideTheBound(t *testing.T) {
	v := NewSlidingWindow()
	if err := v.Accept(1); err != nil {
		t.Fatal(err)
	}
	far := uint64(1 + MaxForwardJump)
	if err := v.Accept(far); err != nil {
		t.Fatal(err)
	}
	if err := v.Accept(far - 1); err != nil {
		t.Errorf("frame just behind the new high water rejected: %v", err)
	}
	if err := v.Accept(far); err != ErrReplay {
		t.Error("high-water frame accepted twice")
	}
}

// A leap past the bound is refused by BOTH validators.
//
// The backward half of the rule stops replays and says nothing about a forward
// leap, which burns nonce space and — with a strict counter — is irreversible:
// every later legitimate frame carries a lower sequence and is refused for
// ever, so the session dies quietly after authenticating perfectly well.
func TestForwardJumpIsBounded(t *testing.T) {
	for name, v := range map[string]SequenceValidator{
		"strict": NewStrictMonotonic(),
		"window": NewSlidingWindow(),
	} {
		if err := v.Accept(10); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if err := v.Accept(10 + MaxForwardJump + 1); err != ErrForwardJump {
			t.Errorf("%s: a jump past the bound was accepted: %v", name, err)
		}
		// The bound must not have advanced the state, or a refused frame would
		// still have done its damage.
		if got := v.Highest(); got != 10 {
			t.Errorf("%s: a refused jump advanced Highest to %d", name, got)
		}
		// And a normal frame still works afterwards.
		if err := v.Accept(11); err != nil {
			t.Errorf("%s: normal frame rejected after a refused jump: %v", name, err)
		}
	}
}

// Exactly at the bound is allowed; one past it is not. Pinned because an
// off-by-one here is invisible until a session dies.
func TestForwardJumpBoundaryIsExact(t *testing.T) {
	v := NewStrictMonotonic()
	if err := v.Accept(1); err != nil {
		t.Fatal(err)
	}
	if err := v.Accept(1 + MaxForwardJump); err != nil {
		t.Errorf("a jump of exactly MaxForwardJump was refused: %v", err)
	}
	v2 := NewStrictMonotonic()
	if err := v2.Accept(1); err != nil {
		t.Fatal(err)
	}
	if err := v2.Accept(2 + MaxForwardJump); err != ErrForwardJump {
		t.Errorf("a jump of MaxForwardJump+1 was accepted: %v", err)
	}
}

// The ordering requirement each validator imposes must be stated by the
// validator, so Session can assert it rather than assume it.
func TestValidatorsDeclareTheirTransportRequirement(t *testing.T) {
	if !NewStrictMonotonic().RequiresOrderedTransport() {
		t.Error("a strict counter must require ordered delivery: it drops anything reordered")
	}
	if NewSlidingWindow().RequiresOrderedTransport() {
		t.Error("a window exists to tolerate reordering and must not require ordering")
	}
}

// --- transcript ---

// The separators are what stop two different (jti, key) pairs producing the
// same transcript. Without them the MAC authenticates both readings equally.
func TestTranscriptIsUnambiguous(t *testing.T) {
	var a, b [PublicKeySize]byte
	a[0], b[0] = 1, 2

	// Two splits that concatenate to the same bytes if nothing separates them.
	t1, err := Transcript("ab", a, b)
	if err != nil {
		t.Fatal(err)
	}
	t2, err := Transcript("a", a, b)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(t1, t2) {
		t.Fatal("transcripts for different jti collided")
	}

	// Swapping the two public keys must change the transcript, or a MITM could
	// reflect a binding back.
	swapped, err := Transcript("ab", b, a)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(t1, swapped) {
		t.Fatal("transcript is symmetric in the two public keys; a reflected " +
			"binding would verify")
	}
}

func TestTranscriptNeedsAJTI(t *testing.T) {
	var a, b [PublicKeySize]byte
	if _, err := Transcript("", a, b); err != ErrBadJTI {
		t.Errorf("err = %v, want ErrBadJTI", err)
	}
}

// Golden vector so the C# and Unity implementations can be checked against this
// one without running all three. Two implementations that each round-trip
// against themselves can still disagree, and the failure is silent: the
// handshake simply never completes.
func TestTranscriptGoldenVector(t *testing.T) {
	var cpub, spub [PublicKeySize]byte
	for i := range cpub {
		cpub[i] = byte(i)
		spub[i] = byte(0x80 + i)
	}
	got, err := Transcript("golden-jti", cpub, spub)
	if err != nil {
		t.Fatal(err)
	}
	const want = "6375766172612f7365616c65642d68616e647368616b652f763100676f6c64656e2d6a746900" +
		"000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f808182838485" +
		"868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"
	if hex.EncodeToString(got) != want {
		t.Errorf("transcript changed\n got: %s\nwant: %s\n"+
			"Every implementation must move together or handshakes stop completing.",
			hex.EncodeToString(got), want)
	}
}

// --- refusal ---

func TestRefusalHasNoMiddleGround(t *testing.T) {
	sealedPeer := PeerCapabilities{SealedHandshakeCompleted: true, EncodingCanSeal: true}
	cleartextPeer := PeerCapabilities{EncodingCanSeal: true}
	jsonPeer := PeerCapabilities{}

	if r := RefusalFor(Disabled, cleartextPeer); r.Refused {
		t.Error("a disabled listener refused a cleartext peer")
	}
	if r := RefusalFor(Required, sealedPeer); r.Refused {
		t.Errorf("a sealed peer was refused: %s", r.Reason)
	}
	if r := RefusalFor(Required, cleartextPeer); !r.Refused || r.Reason != ReasonNoSealedSession {
		t.Errorf("cleartext peer: %+v, want refused/%s", r, ReasonNoSealedSession)
	}
	// The consequence flagged in ADR-22: a JSON client cannot seal, so once
	// encryption is required it is refused rather than served in the clear.
	if r := RefusalFor(Required, jsonPeer); !r.Refused || r.Reason != ReasonEncodingCannot {
		t.Errorf("JSON peer: %+v, want refused/%s", r, ReasonEncodingCannot)
	}
}
