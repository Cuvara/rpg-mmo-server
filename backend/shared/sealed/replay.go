package sealed

import "errors"

// ErrReplay reports a sequence number that has already been accepted, or that
// is too old to judge. It is deliberately one error for both: telling a peer
// WHICH of the two it hit tells an attacker where the window edge is.
var ErrReplay = errors.New("sealed: replayed or out-of-window sequence")

// ErrForwardJump reports a sequence that leaps too far ahead in one frame.
var ErrForwardJump = errors.New("sealed: sequence jumped too far forward")

// MaxForwardJump bounds how far a sequence may advance in a single frame.
//
// # Why the backward half of the rule is not the whole rule
//
// "Reject anything at or below the highest seen" stops replays and says nothing
// about a leap FORWARD. A peer whose counter is corrupted — or is being steered
// by anything that can influence it — can jump to near the top of the space in
// one frame. Two consequences follow, and neither is a replay:
//
//   - it burns nonce space, forcing a rekey far earlier than the traffic
//     justifies;
//   - with a strict counter it is IRREVERSIBLE. Every legitimate frame after it
//     carries a lower sequence and is refused for ever, so the session is dead
//     and the symptom is a connection that authenticated fine and then went
//     quiet.
//
// The exposure is bounded to start with — a forged frame cannot advance
// anything, because it does not authenticate — so this defends against a
// confused or compromised PEER rather than against an outsider. That is worth
// having anyway: the cost of the bound is nothing and the failure without it is
// silent.
//
// 1024 is slack, not a budget. On an ordered, reliable transport the expected
// delta between consecutive frames is exactly 1: the ARQ repairs loss, so a gap
// means something below has already gone wrong. 1024 leaves three orders of
// magnitude of room before it can bite a healthy session, and still bounds the
// damage to something a rekey can absorb.
const MaxForwardJump = 1024

// SequenceValidator decides whether a frame's sequence number is fresh.
//
// It exists as an interface because the answer depends on a transport property
// that is still being measured: whether the ARQ can ever deliver two frames out
// of order at this layer. If it cannot, a strict counter is enough and is the
// cheapest and most obviously correct thing. If it can, a strict counter would
// drop legitimate frames and a window is required.
//
// Both implementations live here so that finding can land as a one-line change
// at the call site rather than as a rewrite — and so the choice is made from a
// measurement rather than from whichever was written first.
//
// IMPLEMENTATIONS MUST BE CALLED ONLY AFTER THE TAG VERIFIES. The sequence is
// cleartext, so accepting it before Open succeeds lets an attacker advance a
// peer's window with forged frames and lock out the real sender — a denial of
// service that costs nothing to mount.
type SequenceValidator interface {
	// Accept records sequence as seen and reports whether it was fresh.
	// A false return means the frame must be dropped and, on this protocol,
	// the session ended: there is no benign reason for a replay.
	Accept(sequence uint64) error

	// Highest returns the largest sequence accepted so far, for diagnostics.
	Highest() uint64

	// RequiresOrderedTransport reports that this validator is only correct when
	// the transport below cannot reorder.
	//
	// It exists so the requirement is ASSERTED rather than assumed. The ordering
	// this design rests on is inherited, not owned: TCP guarantees it, and KCP
	// gets it from a hand-ported reassembly path of roughly ten lines. A future
	// QUIC-datagram or raw-UDP transport would quietly violate it, and with a
	// strict counter the symptom is dropped legitimate frames rather than an
	// error that names the cause. Session refuses to construct in that case, so
	// the new transport fails closed on day one instead of degrading.
	RequiresOrderedTransport() bool
}

// StrictMonotonic accepts strictly increasing sequence numbers and nothing
// else. Correct when the transport below cannot reorder: TCP by definition, and
// KCP in the reliable, ordered mode this project configures.
type StrictMonotonic struct {
	highest uint64
	seen    bool
}

// NewStrictMonotonic returns a validator that requires strictly increasing
// sequences.
func NewStrictMonotonic() *StrictMonotonic { return &StrictMonotonic{} }

// Accept implements SequenceValidator.
func (s *StrictMonotonic) Accept(sequence uint64) error {
	if s.seen {
		if sequence <= s.highest {
			return ErrReplay
		}
		if sequence-s.highest > MaxForwardJump {
			return ErrForwardJump
		}
	}
	s.highest = sequence
	s.seen = true
	return nil
}

// Highest implements SequenceValidator.
func (s *StrictMonotonic) Highest() uint64 { return s.highest }

// RequiresOrderedTransport implements SequenceValidator. A strict counter drops
// any frame that arrives out of order, so it is only correct where none can.
func (s *StrictMonotonic) RequiresOrderedTransport() bool { return true }

// WindowSize is the default width of SlidingWindow, in frames.
//
// 64 is one machine word, so the bitmap is a single uint64 and membership is
// two instructions. It is also far wider than any reordering a reliable ARQ can
// produce, which means the width is not a tuning parameter anyone should have
// to think about — if 64 is ever not enough, the transport is not delivering
// what this layer assumes and that is the thing to fix.
const WindowSize = 64

// SlidingWindow accepts any sequence not already seen and not older than
// WindowSize behind the highest accepted — the rule IPsec and DTLS use.
//
// Required if the transport can reorder. Harmless if it cannot, at the cost of
// one word of state per direction, which is why it is the safer default if the
// measurement comes back ambiguous.
type SlidingWindow struct {
	highest uint64
	bitmap  uint64 // bit i set => (highest - i) has been accepted
	seen    bool
	width   uint
}

// NewSlidingWindow returns a validator with the default width.
func NewSlidingWindow() *SlidingWindow { return NewSlidingWindowOf(WindowSize) }

// NewSlidingWindowOf returns a validator with an explicit width, capped at 64
// because the bitmap is one word.
func NewSlidingWindowOf(width uint) *SlidingWindow {
	if width == 0 || width > WindowSize {
		width = WindowSize
	}
	return &SlidingWindow{width: width}
}

// Accept implements SequenceValidator.
func (w *SlidingWindow) Accept(sequence uint64) error {
	if !w.seen {
		w.seen = true
		w.highest = sequence
		w.bitmap = 1
		return nil
	}

	if sequence > w.highest && sequence-w.highest > MaxForwardJump {
		return ErrForwardJump
	}

	switch {
	case sequence > w.highest:
		// Advance. Frames between the old and new high water are still
		// acceptable if they arrive later, so the bitmap shifts rather than
		// clearing — that is the whole difference from a strict counter.
		shift := sequence - w.highest
		if shift >= uint64(w.width) {
			w.bitmap = 0
		} else {
			w.bitmap <<= shift
		}
		w.bitmap |= 1
		w.highest = sequence
		return nil

	case sequence == w.highest:
		return ErrReplay

	default:
		behind := w.highest - sequence
		if behind >= uint64(w.width) {
			// Too old to judge. Refused rather than accepted: a validator that
			// cannot prove a frame is fresh must not claim that it is.
			return ErrReplay
		}
		mask := uint64(1) << behind
		if w.bitmap&mask != 0 {
			return ErrReplay
		}
		w.bitmap |= mask
		return nil
	}
}

// Highest implements SequenceValidator.
func (w *SlidingWindow) Highest() uint64 { return w.highest }

// RequiresOrderedTransport implements SequenceValidator. A window exists
// precisely to tolerate reordering, so it imposes no such requirement.
func (w *SlidingWindow) RequiresOrderedTransport() bool { return false }
