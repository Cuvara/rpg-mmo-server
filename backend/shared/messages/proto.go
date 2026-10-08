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
		m = authReqPB(t)
	case *AuthRequest:
		m = authReqPB(*t)

	case AuthResponse:
		m = authRespPB(t)
	case *AuthResponse:
		m = authRespPB(*t)

	case EnterWorldRequest:
		m = &wirepb.EnterWorldRequest{MapId: t.MapID, PartyId: t.PartyID, CharacterId: t.CharacterID}
	case *EnterWorldRequest:
		m = &wirepb.EnterWorldRequest{MapId: t.MapID, PartyId: t.PartyID, CharacterId: t.CharacterID}

	case EnterWorldResponse:
		m = enterWorldRespPB(t)
	case *EnterWorldResponse:
		m = enterWorldRespPB(*t)

	case JoinTokenRequest:
		m = joinTokenReqPB(t)
	case *JoinTokenRequest:
		m = joinTokenReqPB(*t)

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

	case TransferMapRequest:
		m = &wirepb.TransferMapRequest{MapId: t.MapID}
	case *TransferMapRequest:
		m = &wirepb.TransferMapRequest{MapId: t.MapID}

	case TransferMapResponse:
		m = transferMapRespPB(t)
	case *TransferMapResponse:
		m = transferMapRespPB(*t)
	case PingMessage:
		m = &wirepb.PingMessage{Timestamp: t.Timestamp}
	case *PingMessage:
		m = &wirepb.PingMessage{Timestamp: t.Timestamp}

	case PongMessage:
		m = &wirepb.PongMessage{Timestamp: t.Timestamp, ServerTime: t.ServerTime}
	case *PongMessage:
		m = &wirepb.PongMessage{Timestamp: t.Timestamp, ServerTime: t.ServerTime}

	case SealedClientHello:
		m = &wirepb.SealedClientHello{PublicKey: t.PublicKey}
	case *SealedClientHello:
		m = &wirepb.SealedClientHello{PublicKey: t.PublicKey}

	case SealedServerHello:
		m = sealedServerHelloPB(t)
	case *SealedServerHello:
		m = sealedServerHelloPB(*t)

	case KickMessage:
		m = &wirepb.KickMessage{Reason: t.Reason}
	case *KickMessage:
		m = &wirepb.KickMessage{Reason: t.Reason}

	case CommandRequest:
		m = &wirepb.CommandRequest{Seq: t.Seq, Opcode: t.Opcode, Payload: t.Payload}
	case *CommandRequest:
		m = &wirepb.CommandRequest{Seq: t.Seq, Opcode: t.Opcode, Payload: t.Payload}

	case CommandResult:
		m = &wirepb.CommandResult{Seq: t.Seq, Ok: t.OK, Error: t.Error, Payload: t.Payload}
	case *CommandResult:
		m = &wirepb.CommandResult{Seq: t.Seq, Ok: t.OK, Error: t.Error, Payload: t.Payload}

	case ServerPush:
		m = &wirepb.ServerPush{Opcode: t.Opcode, Payload: t.Payload}
	case *ServerPush:
		m = &wirepb.ServerPush{Opcode: t.Opcode, Payload: t.Payload}

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
		t.ProtocolVersion = pb.ProtocolVersion

	case *AuthResponse:
		var pb wirepb.AuthResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.OK, t.UserID, t.Error = pb.Ok, pb.UserId, pb.Error
		t.ProtocolVersion = pb.ProtocolVersion

	case *EnterWorldRequest:
		var pb wirepb.EnterWorldRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.MapID, t.PartyID = pb.MapId, pb.PartyId
		t.CharacterID = pb.CharacterId

	case *EnterWorldResponse:
		var pb wirepb.EnterWorldResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.ServerAddr, t.JoinToken = pb.ServerAddr, pb.JoinToken
		t.Transport, t.Error = pb.Transport, pb.Error
		t.ServerPublicKey = pb.ServerPublicKey

	case *SealedClientHello:
		var pb wirepb.SealedClientHello
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.PublicKey = pb.PublicKey

	case *SealedServerHello:
		var pb wirepb.SealedServerHello
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.PublicKey, t.Binding, t.Error = pb.PublicKey, pb.Binding, pb.Error
		t.ServerSignature = pb.ServerSignature

	case *JoinTokenRequest:
		var pb wirepb.JoinTokenRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Token = pb.Token
		t.ProtocolVersion = pb.ProtocolVersion

	case *JoinTokenResponse:
		var pb wirepb.JoinTokenResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.OK, t.UserID, t.Error = pb.Ok, pb.UserId, pb.Error
		t.TickRate = pb.TickRate
		t.ProtocolVersion = pb.ProtocolVersion
		t.CharacterID = pb.CharacterId

	case *InputMessage:
		var pb wirepb.InputMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Tick, t.MoveX, t.MoveY = pb.Tick, pb.MoveX, pb.MoveY
		t.AttackTargetID = pb.AttackTargetId
		t.AbilityID, t.AbilityTargetID = pb.AbilityId, pb.AbilityTargetId
		t.AimX, t.AimY, t.AimZ = pb.AimX, pb.AimY, pb.AimZ
		t.RenderTick, t.RenderAlpha = pb.RenderTick, pb.RenderAlpha
		t.Jump, t.SpawnSeq = pb.Jump, pb.SpawnSeq

	case *SnapshotMessage:
		var pb wirepb.SnapshotMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Tick, t.AckTick, t.Full = pb.Tick, pb.AckTick, pb.Full
		t.AckAppliedTick = pb.AckAppliedTick
		t.Removed = pb.Removed
		// A keyframe with no entities and a delta with no entities are both
		// legal; keep the slice non-nil only when the wire carried one, so
		// round-tripping a JSON-shaped message does not gain an empty slice.
		if len(pb.Events) > 0 {
			evs := make([]GameEvent, len(pb.Events))
			for i, e := range pb.Events {
				evs[i] = GameEvent{
					// Carried through even when this build does not recognise the
					// type: an unknown value belongs to a newer peer, and the
					// decision to ignore it is the consumer's, not the codec's.
					Type:      GameEventType(e.Type),
					Source:    e.Source,
					Target:    e.Target,
					SourceID:  e.SourceId,
					TargetID:  e.TargetId,
					Amount:    e.Amount,
					AbilityID: e.AbilityId,
					Flags:     e.Flags,
					EffectID:  e.EffectId,
				}
			}
			t.Events = evs
		} else {
			t.Events = nil
		}

		if len(pb.Entities) > 0 {
			ents := make([]EntitySnapshot, len(pb.Entities))
			for i, e := range pb.Entities {
				typ := e.TypeName
				if name, ok := entityTypeFromPB[e.Type]; ok {
					typ = name
				}
				ents[i] = EntitySnapshot{
					ID:     e.Id,
					Type:   typ,
					X:      e.X,
					Y:      e.Y,
					HP:     int(e.Hp),
					MaxHP:  int(e.MaxHp),
					Handle: e.Handle,
					Speed:  e.Speed,
					// Unknown future action values are carried through as-is rather
					// than clamped to a known one: a value this build does not
					// recognise belongs to a newer peer, and mapping it onto "idle"
					// would turn "I do not know" into a confident wrong answer.
					FacingBrad: e.FacingBrad,
					Action:     EntityAction(e.Action),
					ActionSeq:  e.ActionSeq,

					ChangedFields:   e.ChangedFields,
					Z:               e.Z,
					VelX:            e.VelX,
					VelY:            e.VelY,
					VelZ:            e.VelZ,
					Owner:           e.Owner,
					OwnerID:         e.OwnerId,
					SpawnSeq:        e.SpawnSeq,
					Stats:           statsFromPB(e.Stats),
					StatsRemoved:    e.StatsRemoved,
					Statuses:        statusesFromPB(e.Statuses),
					StatusesRemoved: e.StatusesRemoved,
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

	case *TransferMapRequest:
		var pb wirepb.TransferMapRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.MapID = pb.MapId

	case *TransferMapResponse:
		var pb wirepb.TransferMapResponse
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.OK, t.Error = pb.Ok, pb.Error
	case *PingMessage:
		var pb wirepb.PingMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Timestamp = pb.Timestamp

	case *PongMessage:
		var pb wirepb.PongMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Timestamp, t.ServerTime = pb.Timestamp, pb.ServerTime

	case *KickMessage:
		var pb wirepb.KickMessage
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Reason = pb.Reason

	case *CommandRequest:
		var pb wirepb.CommandRequest
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Seq, t.Opcode, t.Payload = pb.Seq, pb.Opcode, pb.Payload

	case *CommandResult:
		var pb wirepb.CommandResult
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Seq, t.OK, t.Error, t.Payload = pb.Seq, pb.Ok, pb.Error, pb.Payload

	case *ServerPush:
		var pb wirepb.ServerPush
		if err := proto.Unmarshal(data, &pb); err != nil {
			return wrapUnmarshal(v, err)
		}
		t.Opcode, t.Payload = pb.Opcode, pb.Payload

	default:
		return fmt.Errorf("unmarshal proto payload: unsupported message type %T", v)
	}
	return nil
}

func wrapUnmarshal(v any, err error) error {
	return fmt.Errorf("unmarshal proto payload %T: %w", v, err)
}

func authReqPB(t AuthRequest) *wirepb.AuthRequest {
	return &wirepb.AuthRequest{Token: t.Token, ProtocolVersion: t.ProtocolVersion}
}

func authRespPB(t AuthResponse) *wirepb.AuthResponse {
	return &wirepb.AuthResponse{
		Ok:              t.OK,
		UserId:          t.UserID,
		Error:           t.Error,
		ProtocolVersion: t.ProtocolVersion,
	}
}

func enterWorldRespPB(t EnterWorldResponse) *wirepb.EnterWorldResponse {
	return &wirepb.EnterWorldResponse{
		ServerAddr:      t.ServerAddr,
		JoinToken:       t.JoinToken,
		Transport:       t.Transport,
		Error:           t.Error,
		ServerPublicKey: t.ServerPublicKey,
	}
}

func sealedServerHelloPB(t SealedServerHello) *wirepb.SealedServerHello {
	return &wirepb.SealedServerHello{
		PublicKey:       t.PublicKey,
		Binding:         t.Binding,
		Error:           t.Error,
		ServerSignature: t.ServerSignature,
	}
}

func joinTokenReqPB(t JoinTokenRequest) *wirepb.JoinTokenRequest {
	return &wirepb.JoinTokenRequest{Token: t.Token, ProtocolVersion: t.ProtocolVersion}
}

func joinTokenRespPB(t JoinTokenResponse) *wirepb.JoinTokenResponse {
	return &wirepb.JoinTokenResponse{
		Ok:              t.OK,
		UserId:          t.UserID,
		Error:           t.Error,
		TickRate:        t.TickRate,
		ProtocolVersion: t.ProtocolVersion,
		CharacterId:     t.CharacterID,
	}
}

func transferMapRespPB(t TransferMapResponse) *wirepb.TransferMapResponse {
	return &wirepb.TransferMapResponse{Ok: t.OK, Error: t.Error}
}

func inputPB(t InputMessage) *wirepb.InputMessage {
	return &wirepb.InputMessage{
		Tick:           t.Tick,
		MoveX:          t.MoveX,
		MoveY:          t.MoveY,
		AttackTargetId: t.AttackTargetID,

		AbilityId:       t.AbilityID,
		AbilityTargetId: t.AbilityTargetID,
		// Written unconditionally rather than only when AbilityID is set. The aim is
		// meaningless without an ability and the server says so; gating it here would
		// be a second place that has to agree about which field gates which, and
		// proto3 elides a zero float anyway.
		AimX: t.AimX,
		AimY: t.AimY,
		AimZ: t.AimZ,

		RenderTick:  t.RenderTick,
		RenderAlpha: t.RenderAlpha,
		Jump:        t.Jump,
		SpawnSeq:    t.SpawnSeq,
	}
}

// Entity type names as they appear in the JSON encoding and in the domain
// structs. The enum exists only on the Protobuf wire; every other layer keeps
// speaking strings, so this mapping is the single place the two meet.
//
// An unknown name is NOT an error: it round-trips through EntitySnapshot's
// type_name fallback. A server that learns a new entity kind before the schema
// does must degrade, not break.
var (
	entityTypeToPB = map[string]wirepb.EntityType{
		"player":     wirepb.EntityType_ENTITY_TYPE_PLAYER,
		"mob":        wirepb.EntityType_ENTITY_TYPE_MOB,
		"npc":        wirepb.EntityType_ENTITY_TYPE_NPC,
		"item":       wirepb.EntityType_ENTITY_TYPE_ITEM,
		"projectile": wirepb.EntityType_ENTITY_TYPE_PROJECTILE,
	}
	entityTypeFromPB = func() map[wirepb.EntityType]string {
		m := make(map[wirepb.EntityType]string, len(entityTypeToPB))
		for name, v := range entityTypeToPB {
			m[v] = name
		}
		return m
	}()
)

// EntityTypeNames returns the type names this build can encode as an enum.
// Exported for tests that assert the two languages agree on the mapping.
func EntityTypeNames() []string {
	out := make([]string, 0, len(entityTypeToPB))
	for name := range entityTypeToPB {
		out = append(out, name)
	}
	return out
}

func snapshotPB(t SnapshotMessage) *wirepb.SnapshotMessage {
	pb := &wirepb.SnapshotMessage{
		Tick:           t.Tick,
		AckTick:        t.AckTick,
		Full:           t.Full,
		Removed:        t.Removed,
		AckAppliedTick: t.AckAppliedTick,
	}
	if len(t.Events) > 0 {
		pb.Events = make([]*wirepb.GameEvent, len(t.Events))
		for i, e := range t.Events {
			pb.Events[i] = &wirepb.GameEvent{
				Type:      wirepb.GameEventType(e.Type),
				Source:    e.Source,
				Target:    e.Target,
				SourceId:  e.SourceID,
				TargetId:  e.TargetID,
				Amount:    e.Amount,
				AbilityId: e.AbilityID,
				Flags:     e.Flags,
				EffectId:  e.EffectID,
			}
		}
	}
	if len(t.Entities) > 0 {
		pb.Entities = make([]*wirepb.EntitySnapshot, len(t.Entities))
		for i, e := range t.Entities {
			ent := &wirepb.EntitySnapshot{
				Id:     e.ID,
				X:      e.X,
				Y:      e.Y,
				Hp:     int32(e.HP),
				MaxHp:  int32(e.MaxHP),
				Handle: e.Handle,
				Speed:  e.Speed,
				// Both are plain pass-throughs: the "zero means not sent" contract
				// lives in the encoding itself (facing is biased so no real angle is
				// 0; action reserves 0) rather than in a translation here, which is
				// exactly why those encodings were chosen.
				FacingBrad: e.FacingBrad,
				Action:     wirepb.EntityAction(e.Action),
				ActionSeq:  e.ActionSeq,

				ChangedFields:   e.ChangedFields,
				Z:               e.Z,
				VelX:            e.VelX,
				VelY:            e.VelY,
				VelZ:            e.VelZ,
				Owner:           e.Owner,
				OwnerId:         e.OwnerID,
				SpawnSeq:        e.SpawnSeq,
				Stats:           statsPB(e.Stats),
				StatsRemoved:    e.StatsRemoved,
				Statuses:        statusesPB(e.Statuses),
				StatusesRemoved: e.StatusesRemoved,
			}
			// Enum when we can (2 bytes), name when we cannot (2 + len). Never
			// both: the reader prefers the enum, so setting both would make the
			// larger field pure waste.
			if v, ok := entityTypeToPB[e.Type]; ok {
				ent.Type = v
			} else {
				ent.TypeName = e.Type
			}
			pb.Entities[i] = ent
		}
	}
	return pb
}

func statsPB(in []StatValue) []*wirepb.StatValue {
	if len(in) == 0 {
		return nil
	}
	out := make([]*wirepb.StatValue, len(in))
	for i, v := range in {
		out[i] = &wirepb.StatValue{StatId: v.StatID, Value: v.Value}
	}
	return out
}

func statsFromPB(in []*wirepb.StatValue) []StatValue {
	if len(in) == 0 {
		return nil
	}
	out := make([]StatValue, len(in))
	for i, v := range in {
		out[i] = StatValue{StatID: v.StatId, Value: v.Value}
	}
	return out
}

func statusesPB(in []StatusEffect) []*wirepb.StatusEffect {
	if len(in) == 0 {
		return nil
	}
	out := make([]*wirepb.StatusEffect, len(in))
	for i, v := range in {
		out[i] = &wirepb.StatusEffect{EffectId: v.EffectID, Stacks: v.Stacks, ExpiresTick: v.ExpiresTick, Source: v.Source}
	}
	return out
}

func statusesFromPB(in []*wirepb.StatusEffect) []StatusEffect {
	if len(in) == 0 {
		return nil
	}
	out := make([]StatusEffect, len(in))
	for i, v := range in {
		out[i] = StatusEffect{EffectID: v.EffectId, Stacks: v.Stacks, ExpiresTick: v.ExpiresTick, Source: v.Source}
	}
	return out
}
