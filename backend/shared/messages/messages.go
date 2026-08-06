package messages

import "encoding/json"

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

// Envelope is the top-level wire message. Type selects the payload schema.
//
// Payload is json.RawMessage so it serializes as inline JSON (not base64),
// ensuring wire compatibility with non-Go implementations (e.g. C#/.NET).
type Envelope struct {
	Type    MsgType         `json:"type"`
	Payload json.RawMessage `json:"payload"`
}

// --- Inner message types ---

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
type EntitySnapshot struct {
	ID    string  `json:"id"`
	Type  string  `json:"type"`
	X     float32 `json:"x"`
	Y     float32 `json:"y"`
	HP    int     `json:"hp"`
	MaxHP int     `json:"max_hp"`
}
