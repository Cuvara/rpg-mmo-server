package pgstore

import (
	"context"
	_ "embed"
	"fmt"
)

// schemaSQL is the embedded game-state schema. It is applied by Migrate on
// every startup and every statement in it is idempotent
// (CREATE TABLE/INDEX IF NOT EXISTS), so re-running is a no-op.
//
// A byte-identical copy lives at backend/deploy/db/init-gamestate.sql, mounted
// into the postgres-game container's /docker-entrypoint-initdb.d/.
//
//go:embed schema.sql
var schemaSQL string

// SchemaSQL returns the embedded migration SQL. Exposed for tooling and tests
// that compare it against backend/deploy/db/init-gamestate.sql.
func SchemaSQL() string { return schemaSQL }

// Migrate applies the embedded schema. It is idempotent and safe to call on
// every process start; concurrent callers are serialised by PostgreSQL's own
// DDL locking.
func (s *PostgresPlayerStore) Migrate(ctx context.Context) error {
	if _, err := s.pool.Exec(ctx, schemaSQL); err != nil {
		return fmt.Errorf("pgstore migrate: %w", err)
	}
	return nil
}
