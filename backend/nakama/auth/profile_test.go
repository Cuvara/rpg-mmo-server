package auth

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

func TestEnsureProfile(t *testing.T) {
	tests := []struct {
		name        string
		userID      string
		displayName string
		existing    string // pre-seeded profile JSON, empty = none
		readErr     error
		writeErr    error
		wantCreated bool
		wantErr     bool
		wantName    string
	}{
		{
			name:        "first login creates profile",
			userID:      "user-abcdefgh-1234",
			displayName: "Arthas",
			wantCreated: true,
			wantName:    "Arthas",
		},
		{
			name:        "first login without username gets fallback name",
			userID:      "user-abcdefgh-1234",
			wantCreated: true,
			wantName:    "Player-user-abc",
		},
		{
			name:        "existing login does not overwrite",
			userID:      "user-abcdefgh-1234",
			displayName: "Arthas",
			existing:    `{"level":42,"created_at":1,"display_name":"Old"}`,
			wantCreated: false,
		},
		{
			name:    "empty user id rejected",
			userID:  "",
			wantErr: true,
		},
		{
			name:    "storage read failure propagates",
			userID:  "user-1",
			readErr: errStorage,
			wantErr: true,
		},
		{
			name:     "storage write failure propagates",
			userID:   "user-1",
			writeErr: errStorage,
			wantErr:  true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := newMockStore()
			store.readErr = tt.readErr
			store.writeErr = tt.writeErr
			if tt.existing != "" {
				store.seed(ProfileCollection, ProfileKey, tt.userID, tt.existing)
			}

			created, err := EnsureProfile(context.Background(), store, tt.userID, tt.displayName)
			if tt.wantErr {
				if err == nil {
					t.Fatal("EnsureProfile() expected error, got nil")
				}
				return
			}
			if err != nil {
				t.Fatalf("EnsureProfile() error: %v", err)
			}
			if created != tt.wantCreated {
				t.Fatalf("created = %v, want %v", created, tt.wantCreated)
			}

			if !created {
				if len(store.writes) != 0 {
					t.Errorf("existing profile should not be written, got %d writes", len(store.writes))
				}
				return
			}

			if len(store.writes) != 1 {
				t.Fatalf("writes = %d, want 1", len(store.writes))
			}
			w := store.writes[0]
			if w.Collection != ProfileCollection || w.Key != ProfileKey || w.UserID != tt.userID {
				t.Errorf("write target = %s/%s/%s, want %s/%s/%s", w.Collection, w.Key, w.UserID, ProfileCollection, ProfileKey, tt.userID)
			}
			if w.PermissionWrite != 0 {
				t.Errorf("PermissionWrite = %d, want 0 (server-authoritative)", w.PermissionWrite)
			}

			var p Profile
			if err := json.Unmarshal([]byte(w.Value), &p); err != nil {
				t.Fatalf("unmarshal written profile: %v", err)
			}
			if p.Level != StartingLevel {
				t.Errorf("Level = %d, want %d", p.Level, StartingLevel)
			}
			if p.CreatedAt == 0 {
				t.Error("CreatedAt must be set")
			}
			if p.DisplayName != tt.wantName {
				t.Errorf("DisplayName = %q, want %q", p.DisplayName, tt.wantName)
			}
		})
	}
}

func TestEnsureProfile_Idempotent(t *testing.T) {
	store := newMockStore()
	ctx := context.Background()

	created, err := EnsureProfile(ctx, store, "user-1", "Jaina")
	if err != nil || !created {
		t.Fatalf("first EnsureProfile() = %v, %v; want true, nil", created, err)
	}
	created, err = EnsureProfile(ctx, store, "user-1", "Jaina")
	if err != nil || created {
		t.Fatalf("second EnsureProfile() = %v, %v; want false, nil", created, err)
	}
	if len(store.writes) != 1 {
		t.Errorf("writes = %d, want 1", len(store.writes))
	}
}

func TestAfterAuthenticateHooks(t *testing.T) {
	tests := []struct {
		name       string
		hook       string // "device" | "email"
		userID     string
		username   string
		existing   string
		wantWrites int
		wantErr    bool
	}{
		{"device first login creates profile", "device", "user-1", "Thrall", "", 1, false},
		{"device existing login no write", "device", "user-1", "Thrall", `{"level":5}`, 0, false},
		{"email first login creates profile", "email", "user-2", "Sylvanas", "", 1, false},
		{"email existing login no write", "email", "user-2", "Sylvanas", `{"level":5}`, 0, false},
		{"missing user id is a no-op", "device", "", "", "", 0, false},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			nk := newMockNakama()
			if tt.existing != "" {
				nk.seed(ProfileCollection, ProfileKey, tt.userID, tt.existing)
			}

			ctx := context.Background()
			if tt.userID != "" {
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USER_ID, tt.userID)    //nolint:staticcheck // Nakama uses string context keys
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USERNAME, tt.username) //nolint:staticcheck
			}

			var err error
			switch tt.hook {
			case "device":
				err = AfterAuthenticateDevice(ctx, noopLogger{}, nil, nk, &api.Session{Created: true}, &api.AuthenticateDeviceRequest{})
			case "email":
				err = AfterAuthenticateEmail(ctx, noopLogger{}, nil, nk, &api.Session{Created: true}, &api.AuthenticateEmailRequest{})
			}

			if tt.wantErr != (err != nil) {
				t.Fatalf("hook error = %v, wantErr %v", err, tt.wantErr)
			}
			if got := len(nk.writes); got != tt.wantWrites {
				t.Errorf("writes = %d, want %d", got, tt.wantWrites)
			}
			if tt.wantWrites > 0 {
				var p Profile
				if err := json.Unmarshal([]byte(nk.writes[0].Value), &p); err != nil {
					t.Fatalf("unmarshal profile: %v", err)
				}
				if p.DisplayName != tt.username {
					t.Errorf("DisplayName = %q, want %q", p.DisplayName, tt.username)
				}
			}
		})
	}
}

func TestAfterAuthenticateDevice_StorageError(t *testing.T) {
	nk := newMockNakama()
	nk.readErr = errStorage
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, "user-1") //nolint:staticcheck

	err := AfterAuthenticateDevice(ctx, noopLogger{}, nil, nk, &api.Session{}, &api.AuthenticateDeviceRequest{})
	if err == nil {
		t.Fatal("AfterAuthenticateDevice() expected error, got nil")
	}
	if !errors.Is(err, errStorage) {
		t.Errorf("error = %v, want wrapped %v", err, errStorage)
	}
}

func TestBeforeAuthenticateEmail(t *testing.T) {
	tests := []struct {
		name      string
		in        *api.AuthenticateEmailRequest
		wantErr   error
		wantEmail string
	}{
		{
			name:      "valid credentials normalised",
			in:        emailReq("Player@Example.COM", "sup3rsecret"),
			wantEmail: "player@example.com",
		},
		{
			name:    "invalid email",
			in:      emailReq("nope", "sup3rsecret"),
			wantErr: ErrInvalidEmail,
		},
		{
			name:    "short password",
			in:      emailReq("player@example.com", "abc"),
			wantErr: ErrWeakPassword,
		},
		{
			name:    "nil request",
			in:      nil,
			wantErr: ErrInvalidEmail,
		},
		{
			name:    "nil account",
			in:      &api.AuthenticateEmailRequest{},
			wantErr: ErrInvalidEmail,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			out, err := BeforeAuthenticateEmail(context.Background(), noopLogger{}, nil, nil, tt.in)
			if tt.wantErr != nil {
				if err != tt.wantErr {
					t.Fatalf("BeforeAuthenticateEmail() error = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("BeforeAuthenticateEmail() error: %v", err)
			}
			if got := out.GetAccount().GetEmail(); got != tt.wantEmail {
				t.Errorf("email = %q, want %q", got, tt.wantEmail)
			}
		})
	}
}

func emailReq(email, password string) *api.AuthenticateEmailRequest {
	return &api.AuthenticateEmailRequest{
		Account: &api.AccountEmail{Email: email, Password: password},
	}
}

// compile-time check: runtime.NakamaModule satisfies the narrow profileStore.
var _ profileStore = (runtime.NakamaModule)(nil)
