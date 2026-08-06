package transport

import (
	"crypto/hkdf"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"strings"

	kcp "github.com/xtaci/kcp-go/v5"
)

// KeyEnvVar is the environment variable holding the pre-shared transport key.
// Every process that talks KCP to another process must be given the same value.
const KeyEnvVar = "TRANSPORT_KEY"

// hkdfInfo domain-separates the transport key from any other key that might one
// day be derived from the same passphrase.
const hkdfInfo = "rpg-mmo/transport/kcp/aes-256"

// aesKeySize is the derived key length: AES-256.
const aesKeySize = 32

// DeriveKey turns an operator-supplied TRANSPORT_KEY into the 32-byte AES-256
// key kcp-go's BlockCrypt wants.
//
// Two accepted input forms:
//
//   - 64 hex characters — decoded verbatim as 32 raw bytes. This is the
//     recommended production form (`openssl rand -hex 32`): the operator
//     supplies full entropy and no derivation guesswork is involved.
//   - anything else — treated as a passphrase and stretched with HKDF-SHA256
//     (no salt, fixed info string). HKDF is a key *derivation* function, not a
//     password hash: it spreads whatever entropy the passphrase has over 32
//     bytes, it does not manufacture entropy that was never there. A short
//     passphrase stays brute-forceable, which is why hex is recommended.
//
// The empty string is not a key; callers check that before calling.
func DeriveKey(key string) ([]byte, error) {
	k := strings.TrimSpace(key)
	if k == "" {
		return nil, fmt.Errorf("derive transport key: empty key")
	}
	if len(k) == 2*aesKeySize {
		if raw, err := hex.DecodeString(k); err == nil {
			return raw, nil
		}
		// Not valid hex despite the length — fall through and treat it as a
		// passphrase rather than failing the operator's start-up.
	}
	derived, err := hkdf.Key(sha256.New, []byte(k), nil, hkdfInfo, aesKeySize)
	if err != nil {
		return nil, fmt.Errorf("derive transport key: %w", err)
	}
	return derived, nil
}

// blockCrypt builds the kcp-go BlockCrypt for a key, or (nil, nil) when the key
// is empty (plaintext, the dev default).
//
// AES-256 in kcp-go is applied per UDP datagram, below the KCP ARQ: every
// packet — including the ones carrying the join token — is encrypted, and a
// peer without the key produces datagrams that decrypt to noise and are dropped
// as malformed KCP segments. There is no negotiation and no downgrade path,
// which is exactly what makes "encrypted listener + plaintext dialer" fail
// closed.
func blockCrypt(key string) (kcp.BlockCrypt, error) {
	if strings.TrimSpace(key) == "" {
		return nil, nil
	}
	raw, err := DeriveKey(key)
	if err != nil {
		return nil, err
	}
	bc, err := kcp.NewAESBlockCrypt(raw)
	if err != nil {
		return nil, fmt.Errorf("aes block crypt: %w", err)
	}
	return bc, nil
}
