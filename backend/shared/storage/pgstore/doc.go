// Package pgstore provides PostgreSQL-backed implementations of the
// storage interfaces, targeting the "Game State" PostgreSQL instance
// (separate from the Nakama meta DB).
//
// Currently implemented: storage.PlayerStore (table player_states).
//
// Usage:
//
//	store, err := pgstore.NewPlayerStore(ctx, os.Getenv("GAME_DB_URL"))
//	if err != nil { ... }
//	defer store.Close()
//	if err := store.Migrate(ctx); err != nil { ... }
//
// The connection pool is created with pgxpool; NewPlayerStore pings the
// database before returning, so a bad DSN or a down server fails at boot
// rather than on the first save.
package pgstore
