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
func DecodeBody(data []byte) (Envelope, error) {
	var env Envelope
	if SniffEncoding(data) == EncodingJSON {
		if err := json.Unmarshal(data, &env); err != nil {
			return env, fmt.Errorf("unmarshal envelope: %w", err)
		}
		env.Enc = EncodingJSON
		return env, nil
	}

	var pb wirepb.Envelope
	if err := proto.Unmarshal(data, &pb); err != nil {
		return env, fmt.Errorf("unmarshal proto envelope: %w", err)
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
func NewEnvelopeAs(enc Encoding, msgType MsgType, payload any) (Envelope, error) {
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
