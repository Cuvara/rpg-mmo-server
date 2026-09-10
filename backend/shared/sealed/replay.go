package sealed

import "errors"

// ErrReplay reports a sequence number that has already been accepted, or that
// is too old to judge. It is deliberately one error for both: telling a peer
// WHICH of the two it hit tells an attacker where the window edge is.
var ErrReplay = errors.New("sealed: replayed or out-of-window sequence")

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
	if s.seen && sequence <= s.highest {
		return ErrReplay
	}
	s.highest = sequence
	s.seen = true
	return nil
}

// Highest implements SequenceValidator.
func (s *StrictMonotonic) Highest() uint64 { return s.highest }

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
