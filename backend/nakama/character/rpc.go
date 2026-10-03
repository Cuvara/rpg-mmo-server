package character

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/heroiclabs/nakama-common/runtime"
)

// CreateRequest is the payload of character_create.
type CreateRequest struct {
	Name string `json:"name"`
	// Slot is optional: absent (or negative) picks the lowest free slot.
	Slot *int `json:"slot,omitempty"`
}

// DeleteRequest is the payload of character_delete.
type DeleteRequest struct {
	CharacterID string `json:"character_id"`
}

// ListResponse is returned by character_list.
type ListResponse struct {
	Characters []Character `json:"characters"`
	// MaxSlots echoes MaxCharacterSlots so a client can render empty slots
	// without hardcoding the (placeholder) cap.
	MaxSlots int `json:"max_slots"`
}

// CreateResponse is returned by character_create.
type CreateResponse struct {
	Character Character `json:"character"`
}

// DeleteResponse is returned by character_delete.
type DeleteResponse struct {
	CharacterID string `json:"character_id"`
	Deleted     bool   `json:"deleted"`
}

// callerID returns the authenticated caller's user id, or ErrUnauthenticated
// for a session-less (runtime.http_key) call.
func callerID(ctx context.Context) (string, error) {
	userID, _ := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string)
	if userID == "" {
		return "", ErrUnauthenticated
	}
	return userID, nil
}

// clientError passes a client-facing *runtime.Error through (unwrapping, since
// the retry-exhausted paths wrap ErrRosterBusy with the storage error) and maps
// anything else to ErrInternal, logging the detail: storage errors leak schema.
func clientError(logger runtime.Logger, rpc, userID string, err error) error {
	var rerr *runtime.Error
	if errors.As(err, &rerr) {
		if errors.Is(err, ErrRosterBusy) {
			logger.Warn("%s gave up on contention for %s: %v", rpc, userID, err)
		}
		return rerr
	}
	logger.Error("%s failed for %s: %v", rpc, userID, err)
	return ErrInternal
}

func marshal(v any) (string, error) {
	out, err := json.Marshal(v)
	if err != nil {
		return "", fmt.Errorf("marshal response: %w", err)
	}
	return string(out), nil
}

// ListRPC handles character_list. Client session required; no payload.
func ListRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, _ string) (string, error) {
	return listRPC(ctx, logger, nk)
}

func listRPC(ctx context.Context, logger runtime.Logger, nk Store) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowRosterRead(userID) {
		return "", ErrRateLimited
	}
	cs, err := List(ctx, nk, userID)
	if err != nil {
		return "", clientError(logger, RPCCharacterList, userID, err)
	}
	if cs == nil {
		cs = []Character{} // "characters": [] rather than null
	}
	return marshal(ListResponse{Characters: cs, MaxSlots: MaxCharacterSlots})
}

// CreateRPC handles character_create. Client session required.
func CreateRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return createRPC(ctx, logger, nk, payload)
}

func createRPC(ctx context.Context, logger runtime.Logger, nk Store, payload string) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowRosterWrite(userID) {
		return "", ErrRateLimited
	}
	if payload == "" {
		return "", ErrInvalidPayload
	}
	var req CreateRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", ErrInvalidPayload
	}
	slot := -1
	if req.Slot != nil && *req.Slot >= 0 {
		slot = *req.Slot
	}
	c, err := Create(ctx, nk, userID, req.Name, slot, time.Now)
	if err != nil {
		return "", clientError(logger, RPCCharacterCreate, userID, err)
	}
	logger.Debug("character %s created for %s in slot %d", c.ID, userID, c.Slot)
	return marshal(CreateResponse{Character: c})
}

// DeleteRPC handles character_delete. Client session required.
func DeleteRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return deleteRPC(ctx, logger, nk, payload)
}

func deleteRPC(ctx context.Context, logger runtime.Logger, nk Store, payload string) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowRosterWrite(userID) {
		return "", ErrRateLimited
	}
	var req DeleteRequest
	if payload != "" {
		if err := json.Unmarshal([]byte(payload), &req); err != nil {
			return "", ErrInvalidPayload
		}
	}
	if err := Delete(ctx, nk, userID, req.CharacterID); err != nil {
		return "", clientError(logger, RPCCharacterDelete, userID, err)
	}
	logger.Debug("character %s deleted for %s", req.CharacterID, userID)
	return marshal(DeleteResponse{CharacterID: req.CharacterID, Deleted: true})
}
