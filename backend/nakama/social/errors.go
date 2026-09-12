package social

import "github.com/heroiclabs/nakama-common/runtime"

// gRPC status codes used for the client-facing runtime errors below. Nakama
// returns the code to the caller as an HTTP status, so the choice is part of
// the RPC contract, not a detail: a client has to tell "you cannot do that"
// (never retry) apart from "try again" (retry).
const (
	codeInvalidArgument    = 3
	codeNotFound           = 5
	codeResourceExhausted  = 8
	codeFailedPrecondition = 9
	codeAborted            = 10
	codeInternal           = 13
	codeUnauthenticated    = 16
)

// Client-facing errors returned by the party RPCs.
var (
	// ErrUnauthenticated is returned when a party mutation RPC is called
	// without a client session. party_create, party_join and party_leave act
	// on "the caller", so a runtime.http_key call — which carries no user id —
	// has no subject and is rejected. party_get is deliberately exempt: the
	// gateway calls it server-to-server.
	ErrUnauthenticated = runtime.NewError("unauthenticated", codeUnauthenticated)

	// ErrInvalidPayload is returned when an RPC payload cannot be decoded.
	ErrInvalidPayload = runtime.NewError("invalid payload", codeInvalidArgument)

	// ErrPartyIDRequired is returned when party_id is missing or empty.
	ErrPartyIDRequired = runtime.NewError("party_id is required", codeInvalidArgument)

	// ErrPartyNotFound is returned when no party exists under the given id.
	// A party ceases to exist the moment its last member leaves, so this is
	// the normal answer for a stale party id, not an exceptional one.
	ErrPartyNotFound = runtime.NewError("party not found", codeNotFound)

	// ErrPartyFull is returned when a join would take the party over
	// MaxPartyMembers. FAILED_PRECONDITION, not RESOURCE_EXHAUSTED: the cap is
	// a game rule, and retrying the same call cannot help.
	ErrPartyFull = runtime.NewError("party is full", codeFailedPrecondition)

	// ErrAlreadyInParty is returned when the caller is already a member of a
	// DIFFERENT party. See the one-party-at-a-time note on JoinParty for why
	// this fails instead of silently leaving the old party.
	ErrAlreadyInParty = runtime.NewError("already in a party", codeFailedPrecondition)

	// ErrNotInParty is returned by party_leave when the caller is in no party.
	ErrNotInParty = runtime.NewError("not in a party", codeFailedPrecondition)

	// ErrPartyBusy is returned when maxWriteAttempts optimistic-concurrency
	// retries all lost the version check. ABORTED is the gRPC code for exactly
	// that ("concurrency conflict, such as a read-modify-write conflict"), and
	// unlike the errors above it IS worth retrying.
	ErrPartyBusy = runtime.NewError("party is being modified concurrently, retry", codeAborted)

	// ErrRateLimited is returned when a caller exceeds a party RPC rate limit.
	ErrRateLimited = runtime.NewError("rate limited", codeResourceExhausted)

	// ErrInternal is returned for unexpected storage failures. The detail is
	// logged, not returned: storage error strings leak schema.
	ErrInternal = runtime.NewError("internal error", codeInternal)
)
