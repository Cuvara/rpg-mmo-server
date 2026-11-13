package constants

// DefaultGameID identifies the existing game. ADR-33 specifies that absent means
// the default game; this identity is a wire contract, not configuration.
const DefaultGameID = "cuvara"

// GameOrDefault applies ADR-33's rule that absent means the default game.
// Non-empty ids are returned unchanged, without trimming or validation.
func GameOrDefault(id string) string {
	if id == "" {
		return DefaultGameID
	}
	return id
}
