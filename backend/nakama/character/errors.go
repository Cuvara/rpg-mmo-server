package character

import "github.com/heroiclabs/nakama-common/runtime"

// gRPC status codes used for the client-facing runtime errors below. Nakama
// returns the code to the caller as an HTTP status, so the choice is part of
// the RPC contract: a client must tell "you cannot do that" (never retry)
// apart from "try again" (retry).
const (
	codeInvalidArgument    = 3
	codeNotFound           = 5
	codeResourceExhausted  = 8
	codeFailedPrecondition = 9
	codeAborted            = 10
	codeInternal           = 13
	codeUnauthenticated    = 16
)

// Client-facing errors returned by the roster RPCs (and by gateway_token when
// it checks a character_id).
var (
	// ErrUnauthenticated is returned when a roster RPC is called without a
	// client session. Every roster RPC acts on "the caller"; a
	// runtime.http_key call carries no user id and has no subject.
	ErrUnauthenticated = runtime.NewError("unauthenticated", codeUnauthenticated)

	// ErrInvalidPayload is returned when an RPC payload cannot be decoded.
	ErrInvalidPayload = runtime.NewError("invalid payload", codeInvalidArgument)

	// ErrInvalidName is returned when a character name fails ValidateName.
	ErrInvalidName = runtime.NewError("invalid character name", codeInvalidArgument)

	// ErrInvalidSlot is returned when a requested slot is outside
	// [0, MaxCharacterSlots).
	ErrInvalidSlot = runtime.NewError("invalid character slot", codeInvalidArgument)

	// ErrCharacterIDRequired is returned when character_delete has no
	// character_id.
	ErrCharacterIDRequired = runtime.NewError("character_id is required", codeInvalidArgument)

	// ErrRosterFull is returned when the account already holds
	// MaxCharacterSlots characters. FAILED_PRECONDITION: retrying the same
	// call cannot help.
	ErrRosterFull = runtime.NewError("character roster is full", codeFailedPrecondition)

	// ErrSlotTaken is returned when a create names a slot that already holds a
	// character.
	ErrSlotTaken = runtime.NewError("character slot is taken", codeFailedPrecondition)

	// ErrCharacterNotFound is returned when a character id is not in the
	// caller's roster. It is also the answer for another account's character:
	// "not yours" and "does not exist" are deliberately indistinguishable, so
	// the RPC is not an oracle for other players' character ids.
	ErrCharacterNotFound = runtime.NewError("character not found", codeNotFound)

	// ErrRosterBusy is returned when every optimistic-concurrency attempt lost
	// the storage version check. ABORTED, and worth retrying.
	ErrRosterBusy = runtime.NewError("roster is being modified concurrently, retry", codeAborted)

	// ErrRateLimited is returned when a caller exceeds a roster RPC rate limit.
	ErrRateLimited = runtime.NewError("rate limited", codeResourceExhausted)

	// ErrInternal is returned for unexpected storage failures. The detail is
	// logged, not returned: storage error strings leak schema.
	ErrInternal = runtime.NewError("internal error", codeInternal)
)
