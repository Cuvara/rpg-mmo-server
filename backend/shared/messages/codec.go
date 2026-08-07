package messages

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"

	"google.golang.org/protobuf/proto"

	wirepb "github.com/duycuong/rpg-mmo/shared/proto/gen"
)

// MaxMessageSize is the largest body a single frame may carry (1 MiB).
const MaxMessageSize = 1 << 20

// jsonEnvelopePrefix is the first byte of a JSON-encoded Envelope: '{'.
//
// A Protobuf-encoded Envelope always starts with 0x08 — the tag byte for field 1
// (`type`, varint), which proto3 always emits because Type is >= 1 for every real
// message. 0x7B and 0x08 cannot collide, so one byte is enough to identify the
// encoding of a frame without any negotiation, version field or handshake.
// This is what lets the gateway, the game server and the Unity client be
// upgraded independently and in any order. See shared/docs/DESIGN.md.
const jsonEnvelopePrefix = '{'

// SniffEncoding reports which encoding a frame body uses, based on its first byte.
//
// It is exported because transports that already have the body in hand (and so
// do not go through Decode) still need to classify it.
func SniffEncoding(body []byte) Encoding {
	if len(body) > 0 && body[0] == jsonEnvelopePrefix {
		return EncodingJSON
	}
	return EncodingProto
}

// Encode serializes an Envelope to bytes with a 4-byte length prefix, using the
// encoding recorded on the envelope itself.
func Encode(env Envelope) ([]byte, error) {
	data, err := EncodeBody(env)
	if err != nil {
		return nil, err
	}
	buf := make([]byte, 4+len(data))
	binary.BigEndian.PutUint32(buf[:4], uint32(len(data)))
	copy(buf[4:], data)
	return buf, nil
}

// EncodeBody serializes an Envelope without the length prefix.
func EncodeBody(env Envelope) ([]byte, error) {
	switch env.Enc {
	case EncodingProto:
		data, err := proto.Marshal(&wirepb.Envelope{
			Type:    uint32(env.Type),
			Payload: env.Payload,
		})
		if err != nil {
			return nil, fmt.Errorf("marshal proto envelope: %w", err)
		}
		return data, nil
	default:
		data, err := json.Marshal(env)
		if err != nil {
			return nil, fmt.Errorf("marshal envelope: %w", err)
		}
		return data, nil
	}
}

// DecodeBody parses one frame body into an Envelope, detecting the encoding from
// the bytes themselves.
//
// It fails closed. Sniffing narrows a body to one of two decoders, but it cannot
// tell a Protobuf envelope from arbitrary bytes that merely happen to be valid
// Protobuf: a body beginning 0x12 parses as a well-formed Envelope carrying only
// field 2, leaving Type at 0. Rejecting Type 0 is what turns that from a silent
// half-parse into an error, so it is a correctness guard and not a formality.
func DecodeBody(data []byte) (Envelope, error) {
	var env Envelope
	// An envelope always carries a type >= 1, so its encoding is never zero
	// bytes long in either scheme. Rejecting this explicitly keeps a truncated
	// frame an error rather than a silently well-formed Type 0 message.
	if len(data) == 0 {
		return env, fmt.Errorf("empty envelope body")
	}
	if SniffEncoding(data) == EncodingJSON {
		if err := json.Unmarshal(data, &env); err != nil {
			return env, fmt.Errorf("unmarshal envelope: %w", err)
		}
		env.Enc = EncodingJSON
		if env.Type == 0 {
			return Envelope{}, fmt.Errorf("decode envelope: %w", ErrInvalidMsgType)
		}
		return env, nil
	}

	var pb wirepb.Envelope
	if err := proto.Unmarshal(data, &pb); err != nil {
		return env, fmt.Errorf("unmarshal proto envelope: %w", err)
	}
	if pb.Type == 0 {
		return Envelope{}, fmt.Errorf("decode proto envelope: %w", ErrInvalidMsgType)
	}
	if pb.Type > 0xFF {
		return env, fmt.Errorf("message type out of range: %d", pb.Type)
	}
	env.Type = MsgType(pb.Type)
	env.Payload = pb.Payload
	env.Enc = EncodingProto
	return env, nil
}

// Decode reads one length-prefixed Envelope from a reader, accepting either
// encoding.
func Decode(r io.Reader) (Envelope, error) {
	var env Envelope
	lenBuf := make([]byte, 4)
	if _, err := io.ReadFull(r, lenBuf); err != nil {
		return env, fmt.Errorf("read length: %w", err)
	}
	length := binary.BigEndian.Uint32(lenBuf)
	if length > MaxMessageSize {
		return env, fmt.Errorf("message too large: %d bytes", length)
	}
	data := make([]byte, length)
	if _, err := io.ReadFull(r, data); err != nil {
		return env, fmt.Errorf("read payload: %w", err)
	}
	return DecodeBody(data)
}

// MarshalPayload encodes an inner message type for use in Envelope.Payload.
func MarshalPayload(enc Encoding, v any) ([]byte, error) {
	if enc == EncodingProto {
		return marshalProtoPayload(v)
	}
	return json.Marshal(v)
}

// NewEnvelope creates a JSON Envelope with a marshaled payload.
//
// It is kept as the JSON-specific constructor so that existing callers — and
// anything that must stay byte-compatible with a pre-Protobuf peer — keep their
// current behaviour without change. Use NewEnvelopeAs to select an encoding.
func NewEnvelope(msgType MsgType, payload any) (Envelope, error) {
	return NewEnvelopeAs(EncodingJSON, msgType, payload)
}

// NewEnvelopeAs creates an Envelope with its payload marshaled in the given
// encoding.
//
// A zero msgType is rejected. The whole encoding-detection scheme depends on a
// Protobuf envelope always emitting field 1 — which proto3 only does for a
// non-zero value — so a Type 0 envelope would encode without the 0x08 prefix and
// be sniffed as the wrong encoding by the peer. The current numbering starts at
// iota + 1, but nothing forces it to stay that way, so it is checked here rather
// than assumed.
func NewEnvelopeAs(enc Encoding, msgType MsgType, payload any) (Envelope, error) {
	if msgType == 0 {
		return Envelope{}, ErrInvalidMsgType
	}
	data, err := MarshalPayload(enc, payload)
	if err != nil {
		return Envelope{}, err
	}
	return Envelope{Type: msgType, Payload: data, Enc: enc}, nil
}

// Reply builds a response envelope in the same encoding as the message it
// answers. Servers should use this rather than picking an encoding themselves:
// it is what makes a connection's encoding sticky and lets a Protobuf server
// keep serving a JSON client.
func (e Envelope) Reply(msgType MsgType, payload any) (Envelope, error) {
	return NewEnvelopeAs(e.Enc, msgType, payload)
}
