// Package sealed defines the wire format, replay rule and refusal policy for
// authenticated encryption on the realtime hops. It deliberately contains NO
// cipher, MAC or curve: those arrive with ADR-22's library choice, behind the
// AEAD interface below.
//
// # Where the AEAD sits, and why not at the packet layer
//
// The existing frame is `[4-byte big-endian length][Envelope protobuf]`. The
// KCP path also has a packet-crypt layer under the ARQ (KcpCrypto), but that
// layer is per-LISTENER — kcp-go takes one BlockCrypt for every datagram and
// offers no per-remote key selection — so it cannot carry a per-session key,
// and TCP, which is the default transport, has no such layer at all.
//
// Sealing therefore happens ABOVE the transport, around the Envelope, which
// makes it identical on TCP and KCP and independent of the ARQ. The length
// prefix stays in the clear because it is what finds the frame boundary.
package sealed

import (
	"encoding/binary"
	"errors"
	"fmt"
)

const (
	// Marker is the first byte of a sealed frame body.
	//
	// The codec picks an encoding by sniffing body[0]: 0x08 is Protobuf (an
	// Envelope always starts with field 1, `type`, and type 0 is refused
	// precisely so that byte is stable) and 0x7B is JSON's '{'. A sealed body
	// begins with ciphertext, which is indistinguishable from either, so it
	// needs a marker of its own. 0xC1 cannot be the start of a well-formed
	// Envelope — as a protobuf tag it names field 24, and field 1 is mandatory.
	Marker byte = 0xC1

	// Version is the sealed-format version. It is inside the authenticated data,
	// so an attacker cannot roll a peer back to an older format by editing it.
	Version byte = 1

	// HeaderSize is marker + version + sequence.
	HeaderSize = 1 + 1 + 8

	// NonceSize is 12 bytes, which is what both candidate AEADs take.
	NonceSize = 12

	// TagSize is the authentication tag both candidate AEADs append.
	TagSize = 16
)

var (
	// ErrNotSealed reports a body that is not a sealed frame. The caller decides
	// what that means: during a handshake it is expected, and afterwards it is a
	// peer sending cleartext where a sealed frame is required, which must end
	// the session rather than be accepted.
	ErrNotSealed = errors.New("sealed: body is not a sealed frame")

	// ErrShortFrame reports a body too small to contain a header and a tag.
	ErrShortFrame = errors.New("sealed: frame shorter than header+tag")

	// ErrUnsupportedVersion reports a sealed frame from a future format.
	ErrUnsupportedVersion = errors.New("sealed: unsupported sealed-frame version")
)

// AEAD is the authenticated cipher this package is built around. It is an
// interface, and there is deliberately no implementation here: ADR-22 has not
// settled which library provides ChaCha20-Poly1305 on all three runtimes, and a
// placeholder that "works" would be worse than none — it would let every test
// above it pass while proving nothing about the bytes.
//
// Seal appends the ciphertext and tag to dst and returns it. Open returns the
// plaintext, or an error if the tag does not verify. Both must be constant-time
// in the tag comparison; that is the implementation's responsibility and is the
// main reason this is a library's job rather than ours.
type AEAD interface {
	// NonceSize reports the nonce length in bytes; must be NonceSize.
	NonceSize() int
	// Overhead reports the tag length in bytes; must be TagSize.
	Overhead() int
	// Seal encrypts plaintext with nonce and additional authenticated data.
	Seal(dst, nonce, plaintext, aad []byte) []byte
	// Open decrypts and verifies. A failure must be indistinguishable in timing
	// and in message from any other failure — a peer must not learn WHY.
	Open(dst, nonce, ciphertext, aad []byte) ([]byte, error)
}

// Header is the cleartext preamble of a sealed frame. Every byte of it is
// covered by the AEAD's additional data, so none of it can be edited in flight:
// renumbering a frame to replay it, or lowering the version to reach an older
// format, both invalidate the tag.
type Header struct {
	Version  byte
	Sequence uint64
}

// AppendHeader writes the header bytes to dst and returns the extended slice.
func AppendHeader(dst []byte, h Header) []byte {
	dst = append(dst, Marker, h.Version)
	var seq [8]byte
	binary.BigEndian.PutUint64(seq[:], h.Sequence)
	return append(dst, seq[:]...)
}

// ParseHeader reads the header from the front of a sealed body.
//
// It does NOT authenticate anything: the header is cleartext, and everything it
// says is unverified until the AEAD tag over the whole frame verifies. Callers
// must therefore treat the sequence as a hint for nonce reconstruction, never
// as a fact, and must not act on it before Open succeeds.
func ParseHeader(body []byte) (Header, []byte, error) {
	if len(body) == 0 || body[0] != Marker {
		return Header{}, nil, ErrNotSealed
	}
	if len(body) < HeaderSize+TagSize {
		return Header{}, nil, ErrShortFrame
	}
	v := body[1]
	if v != Version {
		return Header{}, nil, fmt.Errorf("%w: %d", ErrUnsupportedVersion, v)
	}
	return Header{
		Version:  v,
		Sequence: binary.BigEndian.Uint64(body[2:HeaderSize]),
	}, body[HeaderSize:], nil
}

// Nonce builds the AEAD nonce for a sequence number.
//
// It is 4 zero bytes followed by the big-endian sequence. The zero prefix is
// NOT padding for its own sake: it leaves room for a future explicit direction
// or rekey epoch without changing the nonce length or the frame layout.
//
// # Why a bare counter is safe here, and when it would not be
//
// Reusing a (key, nonce) pair with any AEAD in this family is catastrophic — it
// leaks the XOR of two plaintexts and, for Poly1305, can expose the
// authentication key. A counter is only safe because the handshake gives each
// DIRECTION its own key, so the client's sequence 7 and the server's sequence 7
// are encrypted under different keys and never collide. If a future change ever
// makes one key serve both directions, this function must grow a direction byte
// in the prefix on the same day, or the scheme is broken.
//
// The counter must also never wrap. At 8 bytes and one frame per tick per
// connection that is not reachable in any real session, but a rekey is the
// correct response if it ever approaches the limit, not a wrap.
func Nonce(sequence uint64) [NonceSize]byte {
	var n [NonceSize]byte
	binary.BigEndian.PutUint64(n[4:], sequence)
	return n
}
