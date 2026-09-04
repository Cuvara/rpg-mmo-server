package transfer

import (
	"crypto/rand"
	"fmt"
)

// SessionKeySize is the length of a per-session AES-256 key in bytes.
const SessionKeySize = 32

// GenerateSessionKey mints a cryptographically random 32-byte key suitable for
// per-session KCP encryption (ADR-8).
//
// The key is used raw — no derivation step — because it already has full
// entropy from crypto/rand. Both the C# game server and the Go transport layer
// accept raw 32-byte keys directly.
func GenerateSessionKey() ([]byte, error) {
	key := make([]byte, SessionKeySize)
	if _, err := rand.Read(key); err != nil {
		return nil, fmt.Errorf("generate session key: %w", err)
	}
	return key, nil
}
