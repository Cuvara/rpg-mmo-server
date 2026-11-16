package jwt

import (
	"bytes"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/gameid"
)

type header struct {
	Alg string `json:"alg"`
	Typ string `json:"typ"`
}

// ClockSkew is the tolerance applied to exp, iat, and nbf. A token whose exp
// is up to this many seconds in the past, or iat/nbf this far in the future,
// is still accepted to accommodate small differences between service clocks.
const ClockSkew = 5 * time.Second

// Claims holds the JWT payload.
type Claims struct {
	UserID   string `json:"sub"`
	ServerID string `json:"sid,omitempty"`
	Jti      string `json:"jti,omitempty"`
	// CharacterID is the `cid` claim (ADR-31): the roster character this token
	// plays. Nakama puts it in the gateway token after checking the caller owns
	// the character; the gateway copies it into the join token. Empty (claim
	// absent) means the account's default character, which is what every
	// token minted before protocol 3 carries, so readers must tolerate absence.
	CharacterID string `json:"cid,omitempty"`
	// GameID is the `gid` claim (ADR-33). Absence means constants.DefaultGameID;
	// readers must tolerate absence. Issuers populate it for non-default games.
	GameID   string `json:"gid,omitempty"`
	IssuedAt int64  `json:"iat"`
	ExpireAt int64  `json:"exp"`
}

// Game returns the claim's game, defaulting an absent claim.
func (c Claims) Game() string { return constants.GameOrDefault(c.GameID) }

// IsExpired reports an invalid non-positive expiration or an expiration in the
// past, accounting for ClockSkew. Verify also requires exp to be present.
func (c Claims) IsExpired() bool {
	return c.ExpireAt <= 0 || time.Now().Add(-ClockSkew).Unix() > c.ExpireAt
}

var defaultHeader = header{Alg: "HS256", Typ: "JWT"}

// Sign creates a HS256 JWT for the given user ID.
func Sign(userID, secret string, expiry time.Duration) (string, error) {
	return SignWithServer(userID, "", secret, expiry)
}

// SignWithServer creates a HS256 JWT with an optional server ID claim.
// When serverID is non-empty (join tokens), a unique JTI claim is added
// for replay protection.
func SignWithServer(userID, serverID, secret string, expiry time.Duration) (string, error) {
	return SignWithCharacter(userID, serverID, "", secret, expiry)
}

// SignWithCharacter creates a HS256 JWT with optional server ID and character
// ID (`cid`, ADR-31) claims. An empty characterID omits the claim, which
// readers interpret as the account's default character. As with
// SignWithServer, a non-empty serverID (join tokens) adds a unique JTI.
func SignWithCharacter(userID, serverID, characterID, secret string, expiry time.Duration) (string, error) {
	return SignWithGame(userID, serverID, characterID, "", secret, expiry)
}

// SignWithGame creates a token with an optional game ID (ADR-33). Empty and
// default game IDs omit gid to preserve the default game's existing token shape.
func SignWithGame(userID, serverID, characterID, gameID, secret string, expiry time.Duration) (string, error) {
	if gameID != "" && !gameid.ValidGameID(gameID) {
		return "", fmt.Errorf("sign: %w", fmt.Errorf("invalid game ID %q", gameID))
	}
	if gameID == constants.DefaultGameID {
		gameID = ""
	}
	now := time.Now()
	claims := Claims{
		UserID:      userID,
		ServerID:    serverID,
		CharacterID: characterID,
		GameID:      gameID,
		IssuedAt:    now.Unix(),
		ExpireAt:    now.Add(expiry).Unix(),
	}
	if serverID != "" {
		jti, err := generateJTI()
		if err != nil {
			return "", fmt.Errorf("generate jti: %w", err)
		}
		claims.Jti = jti
	}

	hdrJSON, _ := json.Marshal(defaultHeader)
	clmJSON, err := json.Marshal(claims)
	if err != nil {
		return "", fmt.Errorf("marshal claims: %w", err)
	}

	hdrB64 := base64.RawURLEncoding.EncodeToString(hdrJSON)
	clmB64 := base64.RawURLEncoding.EncodeToString(clmJSON)
	sigInput := hdrB64 + "." + clmB64

	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(sigInput))
	sig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))

	return sigInput + "." + sig, nil
}

// verifyHeader reads the JWT header and enforces the only algorithm
// and token type this package supports (HS256 / JWT). This rejects algorithm
// confusion attacks such as `"alg":"none"` before any signature work happens.
func verifyHeader(hdrJSON []byte) error {
	fields, err := decodeJSONObject(hdrJSON)
	if err != nil {
		return fmt.Errorf("parse header: %w", err)
	}
	if _, present := fields["crit"]; present {
		return fmt.Errorf("unsupported critical header")
	}
	alg, err := stringClaim(fields, "alg", true)
	if err != nil {
		return fmt.Errorf("header alg: %w", err)
	}
	if alg != defaultHeader.Alg {
		return fmt.Errorf("unsupported alg %q, want %q", alg, defaultHeader.Alg)
	}
	typ, err := stringClaim(fields, "typ", true)
	if err != nil {
		return fmt.Errorf("header typ: %w", err)
	}
	if typ != defaultHeader.Typ {
		return fmt.Errorf("unsupported typ %q, want %q", typ, defaultHeader.Typ)
	}
	return nil
}

// Verify validates a canonical HS256/JWT token and its exact, case-sensitive
// claims. Only an otherwise valid expired token returns claims with an error;
// every other refusal returns zero Claims (also used by Keyring.Verify).
func Verify(token, secret string) (Claims, error) {
	return verifyAt(token, secret, time.Now())
}

// verifyAt is the deterministic clock seam used by the shared parity vectors.
func verifyAt(token, secret string, now time.Time) (Claims, error) {
	if token == "" {
		return Claims{}, fmt.Errorf("verify: %w", errors.New("invalid token format: empty token"))
	}
	if secret == "" {
		return Claims{}, fmt.Errorf("verify: %w", errors.New("empty secret"))
	}
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return Claims{}, fmt.Errorf("invalid token format")
	}

	var decoded [3][]byte
	for i, part := range parts {
		var err error
		decoded[i], err = decodeSegment(part)
		if err != nil {
			return Claims{}, fmt.Errorf("invalid token segment %d: %w", i+1, err)
		}
	}
	if len(decoded[2]) != sha256.Size {
		return Claims{}, fmt.Errorf("invalid signature length: want %d bytes", sha256.Size)
	}
	if err := verifyHeader(decoded[0]); err != nil {
		return Claims{}, fmt.Errorf("invalid header: %w", err)
	}

	sigInput := parts[0] + "." + parts[1]
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(sigInput))
	if !hmac.Equal(decoded[2], mac.Sum(nil)) {
		return Claims{}, fmt.Errorf("invalid signature")
	}

	claims, err := decodeClaims(decoded[1], now)
	if err != nil {
		return Claims{}, fmt.Errorf("invalid claims: %w", err)
	}

	if now.Add(-ClockSkew).Unix() > claims.ExpireAt {
		return claims, fmt.Errorf("token expired")
	}

	return claims, nil
}

// decodeSegment refuses alternate encodings of the same signed bytes. Go's
// base64 decoder alone tolerates CR/LF and non-zero unused trailing bits.
func decodeSegment(segment string) ([]byte, error) {
	for i := 0; i < len(segment); i++ {
		c := segment[i]
		if !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-') {
			return nil, fmt.Errorf("non-base64url byte at offset %d", i)
		}
	}
	decoded, err := base64.RawURLEncoding.Strict().DecodeString(segment)
	if err != nil {
		return nil, fmt.Errorf("decode base64url: %w", err)
	}
	if base64.RawURLEncoding.EncodeToString(decoded) != segment {
		return nil, fmt.Errorf("non-canonical base64url")
	}
	return decoded, nil
}

// decodeJSONObject retains exact member names, rejecting duplicates (including
// escaped spellings of the same name). Unknown members remain opaque values.
func decodeJSONObject(raw []byte) (map[string]json.RawMessage, error) {
	if !utf8.Valid(raw) {
		return nil, fmt.Errorf("invalid UTF-8")
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	start, err := d.Token()
	if err != nil {
		return nil, fmt.Errorf("read object: %w", err)
	}
	if start != json.Delim('{') {
		return nil, fmt.Errorf("expected JSON object")
	}
	fields := make(map[string]json.RawMessage)
	for d.More() {
		key, err := d.Token()
		if err != nil {
			return nil, fmt.Errorf("read member name: %w", err)
		}
		name, ok := key.(string)
		if !ok {
			return nil, fmt.Errorf("expected member name")
		}
		if _, duplicate := fields[name]; duplicate {
			return nil, fmt.Errorf("duplicate member %q", name)
		}
		var value json.RawMessage
		if err := d.Decode(&value); err != nil {
			return nil, fmt.Errorf("read member %q: %w", name, err)
		}
		fields[name] = value
	}
	if _, err := d.Token(); err != nil {
		return nil, fmt.Errorf("close object: %w", err)
	}
	if _, err := d.Token(); err != io.EOF {
		if err != nil {
			return nil, fmt.Errorf("trailing JSON: %w", err)
		}
		return nil, fmt.Errorf("trailing JSON value")
	}
	return fields, nil
}

func stringClaim(fields map[string]json.RawMessage, name string, required bool) (string, error) {
	raw, present := fields[name]
	if !present && !required {
		return "", nil
	}
	if len(raw) == 0 || raw[0] != '"' {
		return "", fmt.Errorf("%s must be a JSON string", name)
	}
	var value string
	if err := json.Unmarshal(raw, &value); err != nil {
		return "", fmt.Errorf("read %s: %w", name, err)
	}
	return value, nil
}

// integerClaim requires the lexical JSON integer form, excluding fractions,
// exponents, quoted numbers and null, before checking the signed 64-bit range.
func integerClaim(raw json.RawMessage) (int64, error) {
	digits := raw
	if len(digits) > 0 && digits[0] == '-' {
		digits = digits[1:]
	}
	if len(digits) == 0 || len(digits) > 1 && digits[0] == '0' {
		return 0, fmt.Errorf("expected JSON integer")
	}
	for _, c := range digits {
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("expected JSON integer")
		}
	}
	value, err := strconv.ParseInt(string(raw), 10, 64)
	if err != nil {
		return 0, fmt.Errorf("parse int64: %w", err)
	}
	return value, nil
}

func decodeClaims(raw []byte, now time.Time) (Claims, error) {
	fields, err := decodeJSONObject(raw)
	if err != nil {
		return Claims{}, fmt.Errorf("parse payload: %w", err)
	}
	var claims Claims
	for _, field := range []struct {
		name     string
		target   *string
		required bool
	}{
		{"sub", &claims.UserID, true}, {"sid", &claims.ServerID, false},
		{"jti", &claims.Jti, false}, {"cid", &claims.CharacterID, false}, {"gid", &claims.GameID, false},
	} {
		value, err := stringClaim(fields, field.name, field.required)
		if err != nil {
			return Claims{}, fmt.Errorf("read claim %s: %w", field.name, err)
		}
		*field.target = value
	}
	if claims.UserID == "" {
		return Claims{}, fmt.Errorf("sub must not be empty")
	}
	// Empty and explicit default gid remain accepted for existing test tokens.
	if claims.GameID != "" && !gameid.ValidGameID(claims.GameID) {
		return Claims{}, fmt.Errorf("invalid gid %q", claims.GameID)
	}
	claims.ExpireAt, err = integerClaim(fields["exp"])
	if err != nil {
		return Claims{}, fmt.Errorf("read exp: %w", err)
	}
	if claims.ExpireAt <= 0 {
		return Claims{}, fmt.Errorf("exp must be positive")
	}
	latest := now.Add(ClockSkew).Unix()
	if raw, present := fields["iat"]; present {
		claims.IssuedAt, err = integerClaim(raw)
		if err != nil {
			return Claims{}, fmt.Errorf("read iat: %w", err)
		}
		if claims.IssuedAt < 0 || claims.IssuedAt > latest {
			return Claims{}, fmt.Errorf("iat outside allowed time range")
		}
	}
	if raw, present := fields["nbf"]; present {
		nbf, err := integerClaim(raw)
		if err != nil {
			return Claims{}, fmt.Errorf("read nbf: %w", err)
		}
		if nbf > latest {
			return Claims{}, fmt.Errorf("token not yet valid")
		}
	}
	return claims, nil
}

// generateJTI returns a UUID v4 string for use as a unique token identifier.
func generateJTI() (string, error) {
	var buf [16]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	// Set version 4 and variant bits per RFC 4122.
	buf[6] = (buf[6] & 0x0f) | 0x40
	buf[8] = (buf[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
		buf[0:4], buf[4:6], buf[6:8], buf[8:10], buf[10:16]), nil
}

// ParseUnverified decodes a token's claims WITHOUT checking its signature.
//
// # Do not use this to decide anything
//
// It answers "what does this token say", never "is this token genuine". Anyone
// can produce a token whose claims say anything at all, so a caller that
// authorises on the result has no security at all — use Verify for that, and
// note that every call site here is a place a reviewer should look twice.
//
// It exists for one legitimate case: a CLIENT reading the jti out of its own
// join token. The jti anchors the sealed handshake transcript, and a client
// cannot hold JOIN_TOKEN_SECRET — putting that secret in a client binary is the
// pre-shared-key mistake ADR-22 supersedes. The client is not deciding anything
// on these claims; it is naming the session it is already in, and the server
// verifies the same token properly on its own side.
func ParseUnverified(token string) (Claims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return Claims{}, fmt.Errorf("jwt: malformed token: want 3 segments, got %d", len(parts))
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return Claims{}, fmt.Errorf("jwt: decode claims: %w", err)
	}
	var claims Claims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return Claims{}, fmt.Errorf("jwt: parse claims: %w", err)
	}
	return claims, nil
}
