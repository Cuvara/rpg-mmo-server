package transfer

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// ErrNotAPartyMember is returned when the caller named a party they are not in.
//
// It is deliberately distinct from a transport failure: "you are not in that
// party" is the client's fault and must reach them as a refusal they can act
// on, while "Nakama did not answer" is an outage and must reach them as a
// retryable error. Collapsing the two is how an outage gets reported to players
// as a permissions problem, and a permissions problem gets retried forever.
var ErrNotAPartyMember = errors.New("not a member of that party")

// ErrPartyUnknown is returned when the party does not exist at all.
var ErrPartyUnknown = errors.New("party does not exist")

// PartyMembership answers the one question the gateway needs before allocating
// a dungeon instance (ADR-26 decision 3).
//
// Membership lives in Nakama and is NOT mirrored into the gateway or Redis: a
// mirror of an authority is a second authority that disagrees under partition.
// The cost is one internal RPC per dungeon entry -- never per tick, on a path
// that already blocks waiting for a pod to be allocated.
type PartyMembership interface {
	// IsMember reports whether userID is in partyID. It returns ErrPartyUnknown
	// when there is no such party, and a wrapped transport error when the
	// question could not be asked at all.
	IsMember(ctx context.Context, partyID, userID string) (bool, error)
}

// NakamaParty asks Nakama's party_get RPC over the server-to-server HTTP key --
// the same internal channel the game server already uses for rewards (ADR-24).
type NakamaParty struct {
	baseURL string
	httpKey string
	client  *http.Client
}

// NewNakamaParty builds a membership checker.
//
// The timeout is short on purpose. This call sits inside EnterWorldBudget,
// which is itself sized to stay inside the gateway's heartbeat window, and it
// runs BEFORE an allocation that may take seconds of that budget. A slow
// Nakama must cost the entry attempt, not the connection.
func NewNakamaParty(baseURL, httpKey string, timeout time.Duration) *NakamaParty {
	if timeout <= 0 {
		timeout = 2 * time.Second
	}
	return &NakamaParty{
		baseURL: strings.TrimRight(baseURL, "/"),
		httpKey: httpKey,
		client:  &http.Client{Timeout: timeout},
	}
}

// partyGetResponse mirrors the party_get reply. The field names are Nakama's
// side of the contract, verified against backend/nakama/social/party.go rather
// than assumed: the leader field is "leader_id", not "leader".
//
// LeaderID is unused by this check and is decoded anyway, so the struct
// describes the message rather than only the part this caller happens to read.
// A future check ("only the leader may take a party into a dungeon") needs it,
// and a struct that silently drops a field is where that check would start
// life reading an empty string.
// gRPC codes party_get can return, as the module defines them
// (backend/nakama/social/errors.go).
const (
	grpcCodeNotFound = 5  // the party genuinely does not exist
	grpcCodeInternal = 13 // storage failed; an outage, not the caller's fault
)

// classifyPartyError decides whether a non-2xx reply means "that party is gone"
// or "Nakama is having a bad time".
//
// It reads the gRPC code out of the JSON body FIRST and falls back to the HTTP
// status. That order is the opposite of the obvious one, and the reason is that
// the code is the value the party module actually controls: it returns
// runtime.Error{Code: 5} for an absent party and collapses every storage
// failure to 13. The HTTP status in front of that is produced by
// grpc-gateway's own mapping, which nobody here has verified against a running
// Nakama -- it SHOULD be 404 and 500, and "should" is not something to route a
// player-facing refusal on.
//
// Getting this backwards has one specific cost: a Nakama outage reported as
// "that party no longer exists" tells every player in the game that their party
// vanished, and tells the operator nothing.
func classifyPartyError(resp *http.Response) error {
	var body struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Error   string `json:"error"`
	}
	// A body that will not decode is not itself an answer; fall through to the
	// status, which at least distinguishes 4xx from 5xx.
	_ = json.NewDecoder(io.LimitReader(resp.Body, 1<<16)).Decode(&body)

	switch body.Code {
	case grpcCodeNotFound:
		return ErrPartyUnknown
	case grpcCodeInternal:
		return fmt.Errorf("party membership: nakama internal error (code %d): %s", body.Code, body.Message)
	}

	if resp.StatusCode == http.StatusNotFound {
		return ErrPartyUnknown
	}
	return fmt.Errorf("party membership: party_get status %d: %s", resp.StatusCode, body.Message)
}

type partyGetResponse struct {
	PartyID  string   `json:"party_id"`
	LeaderID string   `json:"leader_id"`
	Members  []string `json:"members"`
}

// IsMember implements PartyMembership.
func (n *NakamaParty) IsMember(ctx context.Context, partyID, userID string) (bool, error) {
	if n == nil || n.baseURL == "" {
		return false, fmt.Errorf("party membership: no Nakama URL configured")
	}

	// Nakama's RPC payload is a JSON *string* containing the JSON document, so
	// the request body is a double-encoded object. That is an upstream API
	// shape, not a choice made here.
	inner, err := json.Marshal(map[string]string{"party_id": partyID})
	if err != nil {
		return false, fmt.Errorf("party membership: marshal payload: %w", err)
	}
	body, err := json.Marshal(string(inner))
	if err != nil {
		return false, fmt.Errorf("party membership: wrap payload: %w", err)
	}

	endpoint := fmt.Sprintf("%s/v2/rpc/party_get?http_key=%s", n.baseURL, url.QueryEscape(n.httpKey))
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, strings.NewReader(string(body)))
	if err != nil {
		return false, fmt.Errorf("party membership: build request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := n.client.Do(req)
	if err != nil {
		return false, fmt.Errorf("party membership: call party_get: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		return false, classifyPartyError(resp)
	}

	// The reply is the same double encoding in reverse: {"payload":"<json>"}.
	var envelope struct {
		Payload string `json:"payload"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&envelope); err != nil {
		return false, fmt.Errorf("party membership: decode envelope: %w", err)
	}

	var out partyGetResponse
	if err := json.Unmarshal([]byte(envelope.Payload), &out); err != nil {
		return false, fmt.Errorf("party membership: decode payload: %w", err)
	}
	if out.PartyID == "" && len(out.Members) == 0 {
		return false, ErrPartyUnknown
	}

	for _, m := range out.Members {
		if m == userID {
			return true, nil
		}
	}
	return false, nil
}
