package messages

import (
	"fmt"

	"google.golang.org/protobuf/proto"

	wirepb "github.com/duycuong/rpg-mmo/shared/proto/gen"
)

// This file is the only place where the domain structs in messages.go and the
// generated Protobuf types in shared/proto/gen meet. Keeping the conversion in
// one file means a schema change surfaces here as a compile error rather than as
// a silently unset field somewhere in the gateway.

// marshalProtoPayload serializes a domain message using the generated Protobuf
// bindings.
//
// An empty struct{} is accepted because MsgDisconnect and MsgResync are sent
// with no payload; both encode to zero bytes.
func marshalProtoPayload(v any) ([]byte, error) {
	var m proto.Message

	switch t := v.(type) {
	case struct{}:
		return nil, nil
	case *struct{}:
		return nil, nil
	case ResyncRequest, *ResyncRequest:
		return nil, nil
	case nil:
		return nil, nil

	case AuthRequest:
		m = &wirepb.AuthRequest{Token: t.Token}
	case *AuthRequest:
		m = &wirepb.AuthRequest{Token: t.Token}

	case AuthResponse:
		m = authRespPB(t)
	case *AuthResponse:
		m = authRespPB(*t)

	case EnterWorldRequest:
		m = &wirepb.EnterWorldRequest{MapId: t.MapID}
	case *EnterWorldRequest:
		m = &wirepb.EnterWorldRequest{MapId: t.MapID}

	case EnterWorldResponse:
		m = enterWorldRespPB(t)
	case *EnterWorldResponse:
		m = enterWorldRespPB(*t)

	case JoinTokenRequest:
		m = &wirepb.JoinTokenRequest{Token: t.Token}
	case *JoinTokenRequest:
		m = &wirepb.JoinTokenRequest{Token: t.Token}

	case JoinTokenResponse:
		m = joinTokenRespPB(t)
	case *JoinTokenResponse:
		m = joinTokenRespPB(*t)

	case InputMessage:
		m = inputPB(t)
	case *InputMessage:
		m = inputPB(*t)

	case SnapshotMessage:
		m = snapshotPB(t)
	case *SnapshotMessage:
		m = snapshotPB(*t)

	case DisconnectMessage:
		m = &wirepb.DisconnectMessage{Reason: t.Reason}
	case *DisconnectMessage:
		m = &wirepb.DisconnectMessage{Reason: t.Reason}

	default:
		return nil, fmt.Errorf("marshal proto payload: unsupported message type %T", v)
	}

	data, err := proto.Marshal(m)
	if err != nil {
		return nil, fmt.Errorf("marshal proto payload %T: %w", v, err)
	}
	return data, nil
}

// unmarshalProtoPayload parses a Protobuf payload into a domain message.
func unmarshalProtoPayload(data []byte, v any) error {
	switch t := v.(type) {
	case *struct{}, nil:
		return nil
	case *ResyncRequest:
		return nil

	case *AuthRequest:
		var pb wirepb.AuthRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Token = pb.Token

	case *AuthResponse:
		var pb wirepb.AuthResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.OK, t.UserID, t.Error = pb.Ok, pb.UserId, pb.Error

	case *EnterWorldRequest:
		var pb wirepb.EnterWorldRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.MapID = pb.MapId

	case *EnterWorldResponse:
		var pb wirepb.EnterWorldResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.ServerAddr, t.JoinToken = pb.ServerAddr, pb.JoinToken
		t.Transport, t.Error = pb.Transport, pb.Error

	case *JoinTokenRequest:
		var pb wirepb.JoinTokenRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Token = pb.Token

	case *JoinTokenResponse:
		var pb wirepb.JoinTokenResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.OK, t.UserID, t.Error = pb.Ok, pb.UserId, pb.Error

	case *InputMessage:
		var pb wirepb.InputMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Tick, t.MoveX, t.MoveY = pb.Tick, pb.MoveX, pb.MoveY
		t.AttackTargetID = pb.AttackTargetId

	case *SnapshotMessage:
		var pb wirepb.SnapshotMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Tick, t.AckTick, t.Full = pb.Tick, pb.AckTick, pb.Full
		t.Removed = pb.Removed
		// A keyframe with no entities and a delta with no entities are both
		// legal; keep the slice non-nil only when the wire carried one, so
		// round-tripping a JSON-shaped message does not gain an empty slice.
		if len(pb.Entities) > 0 {
			ents := make([]EntitySnapshot, len(pb.Entities))
			for i, e := range pb.Entities {
				ents[i] = EntitySnapshot{
					ID:    e.Id,
					Type:  e.Type,
					X:     e.X,
					Y:     e.Y,
					HP:    int(e.Hp),
					MaxHP: int(e.MaxHp),
				}
			}
			t.Entities = ents
		} else {
			t.Entities = nil
		}

	case *DisconnectMessage:
		var pb wirepb.DisconnectMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Reason = pb.Reason

	default:
		return fmt.Errorf("unmarshal proto payload: unsupported message type %T", v)
	}
	return nil
}

func wrapUnmarshal(v any, err error) error {
	return fmt.Errorf("unmarshal proto payload %T: %w", v, err)
}

func authRespPB(t AuthResponse) *wirepb.AuthResponse {
	return &wirepb.AuthResponse{Ok: t.OK, UserId: t.UserID, Error: t.Error}
}

func enterWorldRespPB(t EnterWorldResponse) *wirepb.EnterWorldResponse {
	return &wirepb.EnterWorldResponse{
		ServerAddr: t.ServerAddr,
		JoinToken:  t.JoinToken,
		Transport:  t.Transport,
		Error:      t.Error,
	}
}

func joinTokenRespPB(t JoinTokenResponse) *wirepb.JoinTokenResponse {
	return &wirepb.JoinTokenResponse{Ok: t.OK, UserId: t.UserID, Error: t.Error}
}

func inputPB(t InputMessage) *wirepb.InputMessage {
	return &wirepb.InputMessage{
		Tick:           t.Tick,
		MoveX:          t.MoveX,
		MoveY:          t.MoveY,
		AttackTargetId: t.AttackTargetID,
	}
}

func snapshotPB(t SnapshotMessage) *wirepb.SnapshotMessage {
	pb := &wirepb.SnapshotMessage{
		Tick:    t.Tick,
		AckTick: t.AckTick,
		Full:    t.Full,
		Removed: t.Removed,
	}
	if len(t.Entities) > 0 {
		pb.Entities = make([]*wirepb.EntitySnapshot, len(t.Entities))
		for i, e := range t.Entities {
			pb.Entities[i] = &wirepb.EntitySnapshot{
				Id:    e.ID,
				Type:  e.Type,
				X:     e.X,
				Y:     e.Y,
				Hp:    int32(e.HP),
				MaxHp: int32(e.MaxHP),
			}
		}
	}
	return pb
}
