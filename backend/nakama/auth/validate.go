package auth

import (
	"context"
	"database/sql"
	"net/mail"
	"strings"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// ValidateEmailCredentials checks the email format and password strength.
// It returns a client-facing runtime error (ErrInvalidEmail / ErrWeakPassword)
// so Nakama forwards a proper gRPC status code to the client.
func ValidateEmailCredentials(email, password string, minPasswordLength int) error {
	email = strings.TrimSpace(email)
	if email == "" {
		return ErrInvalidEmail
	}
	addr, err := mail.ParseAddress(email)
	if err != nil || addr.Address != email {
		return ErrInvalidEmail
	}
	// mail.ParseAddress accepts local-only addresses in some forms; require a dotted domain.
	at := strings.LastIndex(email, "@")
	if at < 1 || !strings.Contains(email[at+1:], ".") {
		return ErrInvalidEmail
	}
	if len(password) < minPasswordLength {
		return ErrWeakPassword
	}
	return nil
}

// BeforeAuthenticateEmail validates credentials before Nakama processes an
// email authentication request. The email is normalised to lower case.
func BeforeAuthenticateEmail(ctx context.Context, _ runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateEmailRequest) (*api.AuthenticateEmailRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return nil, ErrInvalidEmail
	}
	cfg := LoadConfig(ctx)
	account := in.GetAccount()

	if err := ValidateEmailCredentials(account.GetEmail(), account.GetPassword(), cfg.MinPasswordLength); err != nil {
		return nil, err
	}

	account.Email = strings.ToLower(strings.TrimSpace(account.GetEmail()))
	return in, nil
}
