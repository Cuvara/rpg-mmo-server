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
type SnapshotMessage struct {
	Tick     uint64           `json:"tick"`
	Entities []EntitySnapshot `json:"entities"`
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
