package transfer

import (
	"context"
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

			_, err := NewNakamaParty(srv.URL, "testkey", 0).IsMember(context.Background(), "p1", "u1")
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

	ok, err := p.IsMember(context.Background(), "8f14e45f", "u2")
	if err != nil || !ok {
		t.Fatalf("member u2: ok=%v err=%v", ok, err)
	}

	ok, err = p.IsMember(context.Background(), "8f14e45f", "intruder")
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
