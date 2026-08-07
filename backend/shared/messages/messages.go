package messages

import (
	"encoding/json"
	"errors"
	"fmt"
)

// ErrInvalidMsgType marks an envelope whose type is 0.
//
// Zero is not a message type in either encoding, and it is the value a Protobuf
// decode leaves behind when the bytes never carried field 1 at all — which is
// what arbitrary non-JSON data looks like. Treating it as an error is what makes
// decoding fail closed instead of yielding a typeless envelope.
var ErrInvalidMsgType = errors.New("invalid message type 0")

// ErrUnknownHandle is returned when a snapshot references an interned entity
// handle the receiver has no binding for.
//
// This is not a decode failure — the bytes were well formed. It means the two
// sides disagree about what a handle means, i.e. the receiver has lost state the
// sender assumed it had. Guessing is the one thing a receiver must not do: it
// would attribute an update to the wrong entity, producing wrong state rather
// than absent state, which is far harder to notice.
//
// The correct response is to request a keyframe (MsgResync). A keyframe
// re-introduces every binding and resets the handle space on both sides, so it
// repairs the disagreement whatever caused it.
var ErrUnknownHandle = errors.New("snapshot references an unknown entity handle")

// MsgType identifies the type of message in an Envelope.
type MsgType uint8

const (
	MsgAuth           MsgType = iota + 1 // client -> gateway
	MsgAuthResp                          // gateway -> client
	MsgEnterWorld                        // client -> gateway
	MsgEnterWorldResp                    // gateway -> client
	MsgJoinToken                         // client -> gameserver
	MsgJoinTokenResp                     // gameserver -> client
	MsgInput                             // client -> gameserver (per tick)
	MsgSnapshot                          // gameserver -> client (per tick)
	MsgDisconnect                        // either direction
	MsgResync                            // client -> gameserver (request a full keyframe)
)

// Encoding selects how an Envelope and its payload are serialized.
//
// Both encodings share the same framing ([4-byte big-endian length][body]) and
// the same MsgType space, so they are interchangeable on a per-connection basis
// and the transport layer never has to know which is in use.
type Encoding uint8

const (
	// EncodingJSON is the legacy encoding: {"type":N,"payload":{...}}.
	EncodingJSON Encoding = iota
	// EncodingProto is the Protobuf encoding generated from shared/proto/wire.proto.
	EncodingProto
)

// String implements fmt.Stringer.
func (e Encoding) String() string {
	switch e {
	case EncodingJSON:
		return "json"
	case EncodingProto:
		return "proto"
	default:
		return fmt.Sprintf("Encoding(%d)", uint8(e))
	}
}

// ParseEncoding maps a flag/env string onto an Encoding.
func ParseEncoding(s string) (Encoding, error) {
	switch s {
	case "json", "":
		return EncodingJSON, nil
	case "proto", "protobuf":
		return EncodingProto, nil
	default:
		return 0, fmt.Errorf("unknown wire encoding %q (want \"json\" or \"proto\")", s)
	}
}

// Envelope is the top-level wire message. Type selects the payload schema.
//
// Payload holds the already-serialized inner message, encoded according to Enc.
// For EncodingJSON that is raw JSON (so it marshals inline, not base64, keeping
// wire compatibility with non-Go implementations); for EncodingProto it is the
// Protobuf binary encoding of the corresponding wire.proto message.
//
// Enc is transport metadata, not a wire field: it is set by Decode from the
// bytes actually observed and consumed by Encode to decide how to write. It is
// never itself serialized in either encoding.
type Envelope struct {
	Type    MsgType
	Payload []byte
	Enc     Encoding
}

// envelopeJSON is the on-the-wire JSON shape of an Envelope. It exists so that
// Envelope can carry the Enc field without that field leaking into the JSON.
type envelopeJSON struct {
	Type    MsgType         `json:"type"`
	Payload json.RawMessage `json:"payload"`
}

// MarshalJSON emits the legacy {"type":N,"payload":{...}} shape.
func (e Envelope) MarshalJSON() ([]byte, error) {
	payload := json.RawMessage(e.Payload)
	if len(payload) == 0 {
		payload = json.RawMessage("null")
	}
	return json.Marshal(envelopeJSON{Type: e.Type, Payload: payload})
}

// UnmarshalJSON parses the legacy {"type":N,"payload":{...}} shape.
func (e *Envelope) UnmarshalJSON(data []byte) error {
	var raw envelopeJSON
	if err := json.Unmarshal(data, &raw); err != nil {
		return err
	}
	e.Type = raw.Type
	e.Payload = raw.Payload
	e.Enc = EncodingJSON
	return nil
}

// UnmarshalPayload decodes this envelope's payload into a concrete message type,
// using whichever encoding the envelope arrived in.
//
// This is a method rather than a free function precisely so that a caller cannot
// decode a Protobuf payload as JSON by forgetting to thread the encoding
// through: the encoding travels with the bytes.
func (e Envelope) UnmarshalPayload(v any) error {
	switch e.Enc {
	case EncodingProto:
		return unmarshalProtoPayload(e.Payload, v)
	default:
		return json.Unmarshal(e.Payload, v)
	}
}

// --- Inner message types ---
//
// These stay plain Go structs rather than becoming aliases for the generated
// Protobuf types. They are the domain representation every caller already uses,
// they carry no generated-code baggage (no mutex, no reflection state, cheap to
// copy and compare), and keeping them lets a single codebase speak both
// encodings. shared/messages/proto.go converts them to and from the generated
// types; that conversion is the only place the two representations meet.

// AuthRequest is sent by the client to authenticate with the gateway.
type AuthRequest struct {
	Token string `json:"token"`
}

// AuthResponse is the gateway's reply to an auth request.
type AuthResponse struct {
	OK     bool   `json:"ok"`
	UserID string `json:"user_id,omitempty"`
	Error  string `json:"error,omitempty"`
}

// EnterWorldRequest asks the gateway to assign a map server.
type EnterWorldRequest struct {
	MapID string `json:"map_id"`
}

// EnterWorldResponse contains the game server address and join token.
//
// Transport tells the client which realtime transport the target game server
// speaks ("tcp" or "kcp"). It is omitted when the server speaks TCP, so old
// clients that never read the field keep working — empty means "tcp".
type EnterWorldResponse struct {
	ServerAddr string `json:"server_addr,omitempty"`
	JoinToken  string `json:"join_token,omitempty"`
	Transport  string `json:"transport,omitempty"`
	Error      string `json:"error,omitempty"`
}

// JoinTokenRequest is sent by the client to authenticate with a game server.
type JoinTokenRequest struct {
	Token string `json:"token"`
}

// JoinTokenResponse confirms whether the join was accepted.
type JoinTokenResponse struct {
	OK     bool   `json:"ok"`
	UserID string `json:"user_id,omitempty"`
	Error  string `json:"error,omitempty"`
}

// InputMessage carries player input for one tick.
type InputMessage struct {
	Tick           uint64  `json:"tick"`
	MoveX          float32 `json:"move_x"`
	MoveY          float32 `json:"move_y"`
	AttackTargetID string  `json:"attack_target_id,omitempty"`
}

// SnapshotMessage is a world state update sent to the client.
//
// Snapshots are either KEYFRAMES (Full=true — Entities is the complete AOI set)
// or DELTAS (Full=false — Entities holds only entities that changed since the
// previous snapshot sent to this connection, and Removed lists entities that
// left the AOI or the world). A client reconstructs full state by applying
// deltas onto the last keyframe; see SnapshotState.
//
// All added fields are omitempty, so a pre-delta client that only reads Tick and
// Entities keeps working against a server that only ever sends keyframes.
type SnapshotMessage struct {
	Tick uint64 `json:"tick"`
	// AckTick is the highest client input tick the server has accepted for the
	// receiving player. It is the value the client reconciles its prediction
	// against. Zero means "no input accepted yet".
	AckTick uint64 `json:"ack_tick,omitempty"`
	// Full marks this snapshot as a keyframe: Entities is the authoritative,
	// complete AOI set and the client must discard anything not listed.
	Full     bool             `json:"full,omitempty"`
	Entities []EntitySnapshot `json:"entities"`
	// Removed lists entity IDs that left the AOI (or the world) since the last
	// snapshot. Only meaningful on deltas; always empty on keyframes.
	Removed []string `json:"removed,omitempty"`
}

// EntitySnapshot is a single entity's visible state.
//
// Handle is a per-connection alias for ID on the Protobuf wire, valid until the
// next keyframe. On a message that introduces a handle both fields are set; on
// later mentions only Handle is. Zero means "not interned".
//
// The JSON encoding never interns — Handle is omitted there and ID is always
// present — so a pre-interning client is unaffected.
type EntitySnapshot struct {
	ID     string  `json:"id"`
	Type   string  `json:"type"`
	X      float32 `json:"x"`
	Y      float32 `json:"y"`
	HP     int     `json:"hp"`
	MaxHP  int     `json:"max_hp"`
	Handle uint32  `json:"-"`
}

// DisconnectMessage ends a session politely. Both encodings accept an empty
// payload here, which is what every current sender emits.
type DisconnectMessage struct {
	Reason string `json:"reason,omitempty"`
}

// ResyncRequest asks the game server to make the next snapshot a full keyframe.
// It carries no fields.
type ResyncRequest struct{}
