package auth

import (
	"errors"
	"testing"
)

func TestValidateEmailCredentials(t *testing.T) {
	tests := []struct {
		name     string
		email    string
		password string
		minLen   int
		want     error
	}{
		{"valid", "player@example.com", "sup3rsecret", 8, nil},
		{"valid min length password", "a@b.co", "12345678", 8, nil},
		{"empty email", "", "sup3rsecret", 8, ErrInvalidEmail},
		{"no at sign", "playerexample.com", "sup3rsecret", 8, ErrInvalidEmail},
		{"no domain dot", "player@localhost", "sup3rsecret", 8, ErrInvalidEmail},
		{"display name form rejected", "Player <p@example.com>", "sup3rsecret", 8, ErrInvalidEmail},
		{"spaces inside", "pla yer@example.com", "sup3rsecret", 8, ErrInvalidEmail},
		{"empty local part", "@example.com", "sup3rsecret", 8, ErrInvalidEmail},
		{"short password", "player@example.com", "short", 8, ErrWeakPassword},
		{"empty password", "player@example.com", "", 8, ErrWeakPassword},
		{"custom min length ok", "player@example.com", "abcd", 4, nil},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := ValidateEmailCredentials(tt.email, tt.password, tt.minLen)
			if !errors.Is(err, tt.want) {
				t.Fatalf("ValidateEmailCredentials(%q, %q) error = %v, want %v", tt.email, tt.password, err, tt.want)
			}
		})
	}
}

func TestValidateEmailCredentials_ErrorCodes(t *testing.T) {
	if ErrInvalidEmail.Code != codeInvalidArgument {
		t.Errorf("ErrInvalidEmail code = %d, want %d", ErrInvalidEmail.Code, codeInvalidArgument)
	}
	if ErrWeakPassword.Code != codeInvalidArgument {
		t.Errorf("ErrWeakPassword code = %d, want %d", ErrWeakPassword.Code, codeInvalidArgument)
	}
	if ErrUnauthenticated.Code != codeUnauthenticated {
		t.Errorf("ErrUnauthenticated code = %d, want %d", ErrUnauthenticated.Code, codeUnauthenticated)
	}
}
