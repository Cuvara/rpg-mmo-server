package transfer

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
)

// The classification is keyed on the gRPC code in the body rather than the HTTP
// status, because the code is the value backend/nakama/social controls and the
// status in front of it is grpc-gateway's mapping, which nobody has verified
// against a running Nakama. These cases pin that order.
func TestPartyErrorsAreClassifiedByCodeNotStatus(t *testing.T) {
	tests := []struct {
		name       string
		status     int
		body       string
		wantUnkown bool
		wantErr    bool
	}{
		{
			name:       "absent party, mapped status",
			status:     http.StatusNotFound,
			body:       `{"error":"party not found","code":5,"message":"party not found"}`,
			wantUnkown: true,
		},
		{
			// The case the caveat is about: if grpc-gateway does NOT map 5 to
			// 404, the code must still decide.
			name:       "absent party, unmapped status",
			status:     http.StatusInternalServerError,
			body:       `{"error":"party not found","code":5,"message":"party not found"}`,
			wantUnkown: true,
		},
		{
			// The expensive inversion. A storage outage arriving as 404 must
			// NOT become "your party no longer exists" -- that tells every
			// player in the game their party vanished and tells the operator
			// nothing.
			name:    "outage wearing a 404",
			status:  http.StatusNotFound,
			body:    `{"error":"internal error","code":13,"message":"internal error"}`,
			wantErr: true,
		},
		{
			name:    "outage, mapped status",
			status:  http.StatusInternalServerError,
			body:    `{"error":"internal error","code":13,"message":"internal error"}`,
			wantErr: true,
		},
		{
			// No decodable body: fall back to the status, which at least
			// separates 4xx from 5xx.
			name:       "undecodable body with 404",
			status:     http.StatusNotFound,
			body:       `<html>502 Bad Gateway</html>`,
			wantUnkown: true,
		},
		{
			name:    "undecodable body with 503",
			status:  http.StatusServiceUnavailable,
			body:    `<html>503</html>`,
			wantErr: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(tt.status)
				_, _ = w.Write([]byte(tt.body))
			}))
			defer srv.Close()

			_, err := NewNakamaParty(srv.URL, "testkey", 0).IsMember(context.Background(), "cuvara", "p1", "u1")
			switch {
			case tt.wantUnkown:
				if !errors.Is(err, ErrPartyUnknown) {
					t.Fatalf("err = %v, want ErrPartyUnknown", err)
				}
			case tt.wantErr:
				if err == nil {
					t.Fatal("want an error")
				}
				if errors.Is(err, ErrPartyUnknown) || errors.Is(err, ErrNotAPartyMember) {
					t.Fatalf("an outage was classified as a client fault: %v", err)
				}
			}
		})
	}
}

// The happy path, including the field name the module actually ships
// (leader_id, not leader) and the double JSON encoding Nakama's RPC uses.
func TestPartyGetDecodesTheShippedShape(t *testing.T) {
	const payload = `{"party_id":"8f14e45f","leader_id":"u1","members":["u1","u2"],"member_count":2,"max_members":4}`

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		// Nakama wraps the RPC result as {"payload":"<json string>"}.
		_ = writeJSONString(w, payload)
	}))
	defer srv.Close()

	p := NewNakamaParty(srv.URL, "testkey", 0)

	ok, err := p.IsMember(context.Background(), "cuvara", "8f14e45f", "u2")
	if err != nil || !ok {
		t.Fatalf("member u2: ok=%v err=%v", ok, err)
	}

	ok, err = p.IsMember(context.Background(), "cuvara", "8f14e45f", "intruder")
	if err != nil {
		t.Fatalf("non-member lookup errored: %v", err)
	}
	if ok {
		t.Fatal("an outsider was reported as a member")
	}
}

func writeJSONString(w http.ResponseWriter, payload string) error {
	// Hand-rolled rather than json.Marshal of a struct so the test asserts the
	// exact envelope shape rather than trusting the same code path twice.
	escaped := ""
	for _, r := range payload {
		switch r {
		case '"':
			escaped += `\"`
		case '\\':
			escaped += `\\`
		default:
			escaped += string(r)
		}
	}
	_, err := w.Write([]byte(`{"payload":"` + escaped + `"}`))
	return err
}

func TestPartyGetPayloadCarriesGameID(t *testing.T) {
	for _, game := range []string{"", "cuvara", "ember"} {
		t.Run("game="+game, func(t *testing.T) {
			wantGame := game
			if wantGame == "" {
				wantGame = "cuvara"
			}
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodPost || r.URL.Path != "/v2/rpc/party_get" || r.URL.Query().Get("http_key") != "testkey" {
					t.Errorf("unexpected RPC request: %s %s", r.Method, r.URL)
				}
				var inner string
				if err := json.NewDecoder(r.Body).Decode(&inner); err != nil {
					t.Errorf("decode RPC envelope: %v", err)
					w.WriteHeader(http.StatusBadRequest)
					return
				}
				var payload map[string]string
				if err := json.Unmarshal([]byte(inner), &payload); err != nil {
					t.Errorf("decode RPC payload: %v", err)
					w.WriteHeader(http.StatusBadRequest)
					return
				}
				if payload["game_id"] != wantGame || payload["party_id"] != "p1" {
					t.Errorf("party_get payload = %v, want game_id=%s party_id=p1", payload, wantGame)
				}
				w.Header().Set("Content-Type", "application/json")
				_ = writeJSONString(w, `{"party_id":"p1","leader_id":"u1","members":["u1"]}`)
			}))
			defer srv.Close()
			ok, err := NewNakamaParty(srv.URL, "testkey", 0).IsMember(context.Background(), game, "p1", "u1")
			if err != nil || !ok {
				t.Fatalf("IsMember: ok=%v err=%v", ok, err)
			}
		})
	}
}
