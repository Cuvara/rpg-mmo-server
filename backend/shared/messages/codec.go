package messages

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
)

// Encode serializes an Envelope to bytes with a 4-byte length prefix.
func Encode(env Envelope) ([]byte, error) {
	data, err := json.Marshal(env)
	if err != nil {
		return nil, fmt.Errorf("marshal envelope: %w", err)
	}
	buf := make([]byte, 4+len(data))
	binary.BigEndian.PutUint32(buf[:4], uint32(len(data)))
	copy(buf[4:], data)
	return buf, nil
}

// Decode reads one length-prefixed Envelope from a reader.
func Decode(r io.Reader) (Envelope, error) {
	var env Envelope
	lenBuf := make([]byte, 4)
	if _, err := io.ReadFull(r, lenBuf); err != nil {
		return env, fmt.Errorf("read length: %w", err)
	}
	length := binary.BigEndian.Uint32(lenBuf)
	if length > 1<<20 { // 1MB max message
		return env, fmt.Errorf("message too large: %d bytes", length)
	}
	data := make([]byte, length)
	if _, err := io.ReadFull(r, data); err != nil {
		return env, fmt.Errorf("read payload: %w", err)
	}
	if err := json.Unmarshal(data, &env); err != nil {
		return env, fmt.Errorf("unmarshal envelope: %w", err)
	}
	return env, nil
}

// MarshalPayload encodes an inner message type to JSON for use in Envelope.Payload.
func MarshalPayload(v any) ([]byte, error) {
	return json.Marshal(v)
}

// UnmarshalPayload decodes an Envelope's payload into a concrete message type.
func UnmarshalPayload(data []byte, v any) error {
	return json.Unmarshal(data, v)
}

// NewEnvelope creates an Envelope with a JSON-marshaled payload.
func NewEnvelope(msgType MsgType, payload any) (Envelope, error) {
	data, err := MarshalPayload(payload)
	if err != nil {
		return Envelope{}, err
	}
	return Envelope{Type: msgType, Payload: data}, nil
}
