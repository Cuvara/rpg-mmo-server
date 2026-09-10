package sealed

import (
	"errors"
	"fmt"
)

// ErrSessionFailed reports a frame that did not authenticate, or that
// authenticated but was a replay. It is ONE error for both on purpose: the
// caller's correct response is identical — end the session — and telling a peer
// which of the two it hit tells an attacker whether a forged tag got as far as
// the replay window.
var ErrSessionFailed = errors.New("sealed: frame rejected")

// Session seals and opens frames for one direction pair, and is the reason the
// "check the sequence only after the tag verifies" rule cannot be got wrong.
//
// # Why this type exists rather than a documented convention
//
// The rule is easy to state and easy to lose. The sequence number is cleartext
// and sits right there in the header, so the natural-looking implementation
// reads it, checks the replay window, and only then spends CPU on the AEAD —
// which is precisely backwards. That ordering lets an attacker replay a
// captured header with garbage after it and advance the peer's window without
// holding any key, locking out the real sender. It costs nothing to mount and
// would present as a connectivity bug, in the wrong layer, for as long as it
// took someone to suspect it.
//
// A comment does not survive an optimisation pass. So Open below performs both
// steps itself, in the only correct order, and does not expose a way to do one
// without the other: there is no call site left that can reorder them.
type Session struct {
	aead      AEAD
	sendSeq   uint64
	validator SequenceValidator
	sealBuf   []byte
	openBuf   []byte
}

// NewSession builds a session around an AEAD and a replay validator.
//
// aead must be keyed for ONE direction. Passing the same key for both
// directions makes the counter nonce repeat across them, which is the
// catastrophic case documented on Nonce — so the two directions must be
// constructed separately, from separately derived keys.
func NewSession(aead AEAD, validator SequenceValidator) (*Session, error) {
	if aead == nil {
		return nil, errors.New("sealed: nil AEAD")
	}
	if validator == nil {
		return nil, errors.New("sealed: nil sequence validator")
	}
	if aead.NonceSize() != NonceSize {
		return nil, fmt.Errorf("sealed: AEAD nonce size %d, want %d", aead.NonceSize(), NonceSize)
	}
	if aead.Overhead() != TagSize {
		return nil, fmt.Errorf("sealed: AEAD overhead %d, want %d", aead.Overhead(), TagSize)
	}
	return &Session{aead: aead, validator: validator}, nil
}

// Seal wraps one Envelope's bytes as a sealed frame body.
//
// The returned slice is valid until the next Seal on this session — the same
// reuse contract SnapshotFrameWriter already follows, and for the same reason:
// one buffer per connection instead of one allocation per frame.
func (s *Session) Seal(envelope []byte) ([]byte, error) {
	seq := s.sendSeq
	// Increment before use is deliberate: an early return must not leave the
	// counter reusable. A nonce reused after a failed send is the same
	// catastrophe as one reused deliberately.
	s.sendSeq++

	s.sealBuf = s.sealBuf[:0]
	s.sealBuf = AppendHeader(s.sealBuf, Header{Version: Version, Sequence: seq})
	header := s.sealBuf[:HeaderSize]

	nonce := Nonce(seq)
	s.sealBuf = s.aead.Seal(s.sealBuf, nonce[:], envelope, header)
	return s.sealBuf, nil
}

// Open verifies and unwraps a sealed frame body, then checks it for replay.
//
// The order here is the whole point of this type and must not be rearranged:
// the AEAD runs FIRST, and the sequence is offered to the validator only once
// the tag has proved the header was not forged.
//
// A single error is returned for every failure. The caller's response is the
// same in all cases — close the session — and distinguishing them would leak
// which stage an attacker reached.
func (s *Session) Open(body []byte) ([]byte, error) {
	header, ciphertext, err := ParseHeader(body)
	if err != nil {
		return nil, err // not a sealed frame at all; caller distinguishes this one
	}

	// STEP 1: authenticate. Nothing below this line may act on anything the
	// header claimed until this succeeds.
	nonce := Nonce(header.Sequence)
	s.openBuf = s.openBuf[:0]
	plaintext, err := s.aead.Open(s.openBuf, nonce[:], ciphertext, body[:HeaderSize])
	if err != nil {
		return nil, ErrSessionFailed
	}

	// STEP 2: only now is the sequence a fact rather than a claim.
	if err := s.validator.Accept(header.Sequence); err != nil {
		return nil, ErrSessionFailed
	}

	s.openBuf = plaintext
	return plaintext, nil
}

// NextSendSequence reports the sequence the next Seal will use. Diagnostics.
func (s *Session) NextSendSequence() uint64 { return s.sendSeq }

// HighestReceived reports the highest sequence accepted. Diagnostics.
func (s *Session) HighestReceived() uint64 { return s.validator.Highest() }
