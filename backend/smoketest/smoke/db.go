package smoke

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// ---------------------------------------------------------------------------
// Nakama meta-database assertions.
//
// These go through Nakama's PUBLIC HTTP API rather than SQL against the meta
// database. Two reasons: the API works unchanged against a remote VPS where the
// meta postgres is not reachable from the runner, and it asserts the datum the
// way a real client observes it. Nothing here needs a credential the realtime
// smoke flow did not already have, so both checks run on the default path.
//
// SQL is used only for the game-state database (below), because Nakama has no
// visibility into it at all -- ADR-1 keeps the two instances separate and
// nothing exposes player_states over HTTP.
// ---------------------------------------------------------------------------

// nakamaAccount is the subset of Nakama's GET /v2/account response we assert on.
type nakamaAccount struct {
	User struct {
		ID       string `json:"id"`
		Username string `json:"username"`
	} `json:"user"`
	Devices []struct {
		ID string `json:"id"`
	} `json:"devices"`
}

// storageObject is one element of the ReadStorageObjects response.
type storageObject struct {
	Collection string `json:"collection"`
	Key        string `json:"key"`
	UserID     string `json:"user_id"`
	Value      string `json:"value"`
	Version    string `json:"version"`
}

// ProfileValue mirrors nakama/auth.Profile -- the JSON the plugin's
// AfterAuthenticate hook writes to collection "player", key "profile".
type ProfileValue struct {
	Level       int    `json:"level"`
	CreatedAt   int64  `json:"created_at"`
	DisplayName string `json:"display_name"`
}

// getJSON performs an authenticated GET and decodes the JSON body into out.
func (r *Runner) getJSON(path string, out any) error {
	url := strings.TrimRight(r.cfg.NakamaURL, "/") + path
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+r.sessionToken)
	resp, err := r.hc.Do(req)
	if err != nil {
		return fmt.Errorf("GET %s: %w", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s: status %d: %s", url, resp.StatusCode, truncate(raw, 200))
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return fmt.Errorf("GET %s: decode: %w", url, err)
	}
	return nil
}

// postJSON performs an authenticated POST and decodes the JSON body into out.
func (r *Runner) postJSON(path string, body any, out any) error {
	url := strings.TrimRight(r.cfg.NakamaURL, "/") + path
	enc, err := json.Marshal(body)
	if err != nil {
		return err
	}
	req, err := http.NewRequest(http.MethodPost, url, strings.NewReader(string(enc)))
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+r.sessionToken)
	req.Header.Set("Content-Type", "application/json")
	resp, err := r.hc.Do(req)
	if err != nil {
		return fmt.Errorf("POST %s: %w", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("POST %s: status %d: %s", url, resp.StatusCode, truncate(raw, 200))
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return fmt.Errorf("POST %s: decode: %w", url, err)
	}
	return nil
}

// ------------------------------------------------------------ nakama_account

// stepNakamaAccount asserts the device login actually created a durable account
// row: Nakama echoes back the same user id the gateway_token RPC issued a JWT
// for, and the device id we authenticated with is linked to it.
//
// This is the assertion that would catch "auth succeeds but nothing is
// persisted" -- a session token is minted in-process and proves nothing about
// the meta database on its own.
func (r *Runner) stepNakamaAccount() (string, error) {
	var acct nakamaAccount
	if err := r.getJSON("/v2/account", &acct); err != nil {
		return "", fmt.Errorf("read account: %w", err)
	}
	if acct.User.ID == "" {
		return "", fmt.Errorf("account has no user id -- nothing was persisted for this login")
	}
	if acct.User.ID != r.userID {
		return "", fmt.Errorf("account user id %q != gateway_token user id %q", acct.User.ID, r.userID)
	}
	if acct.User.Username == "" {
		return "", fmt.Errorf("account %s has an empty username", acct.User.ID)
	}
	found := false
	for _, d := range acct.Devices {
		if d.ID == r.deviceID {
			found = true
			break
		}
	}
	if !found {
		ids := make([]string, 0, len(acct.Devices))
		for _, d := range acct.Devices {
			ids = append(ids, d.ID)
		}
		return "", fmt.Errorf("device %q not linked to account %s (linked devices: %v)",
			r.deviceID, acct.User.ID, ids)
	}
	r.username = acct.User.Username
	return fmt.Sprintf("user=%s username=%s devices=%d", acct.User.ID, acct.User.Username, len(acct.Devices)), nil
}

// ------------------------------------------------------------ nakama_profile

// stepNakamaProfile asserts the plugin's AfterAuthenticateDevice hook actually
// wrote the player profile the way nakama/auth/profile.go claims: collection
// "player", key "profile", owned by the player, level == StartingLevel.
//
// ReadStorageObjects returns HTTP 200 with an EMPTY object list when the record
// does not exist, so "no error" is not evidence -- the emptiness check below is
// what makes this able to fail. A Nakama running without our plugin loaded
// authenticates fine and writes no profile at all; that is exactly the
// regression this catches.
func (r *Runner) stepNakamaProfile() (string, error) {
	const (
		collection = "player"  // nakama/auth.ProfileCollection
		key        = "profile" // nakama/auth.ProfileKey
		startLevel = 1         // nakama/auth.StartingLevel
	)
	req := map[string]any{"object_ids": []map[string]string{{
		"collection": collection, "key": key, "user_id": r.userID,
	}}}
	var out struct {
		Objects []storageObject `json:"objects"`
	}
	if err := r.postJSON("/v2/storage", req, &out); err != nil {
		return "", fmt.Errorf("read storage %s/%s: %w", collection, key, err)
	}
	if len(out.Objects) == 0 {
		return "", fmt.Errorf("no storage object at collection=%s key=%s user_id=%s -- "+
			"the AfterAuthenticate profile hook did not write (is the Go plugin loaded?)",
			collection, key, r.userID)
	}
	obj := out.Objects[0]
	if obj.Collection != collection || obj.Key != key {
		return "", fmt.Errorf("storage object is %s/%s, want %s/%s", obj.Collection, obj.Key, collection, key)
	}
	if obj.UserID != r.userID {
		return "", fmt.Errorf("profile owned by %q, want %q", obj.UserID, r.userID)
	}
	var p ProfileValue
	if err := json.Unmarshal([]byte(obj.Value), &p); err != nil {
		return "", fmt.Errorf("profile value is not a Profile record (%s): %w", truncate([]byte(obj.Value), 120), err)
	}
	if p.Level != startLevel {
		return "", fmt.Errorf("profile level = %d, want %d", p.Level, startLevel)
	}
	if p.DisplayName == "" {
		return "", fmt.Errorf("profile display_name is empty")
	}
	if p.CreatedAt <= 0 {
		return "", fmt.Errorf("profile created_at = %d, want a unix timestamp > 0", p.CreatedAt)
	}
	return fmt.Sprintf("%s/%s level=%d display_name=%s", collection, key, p.Level, p.DisplayName), nil
}

// ---------------------------------------------------------------------------
// Game-state database assertions (direct SQL).
// ---------------------------------------------------------------------------

// connectDB opens a short-lived connection to the game-state database.
func (r *Runner) connectDB(ctx context.Context) (*pgx.Conn, error) {
	conn, err := pgx.Connect(ctx, r.cfg.GameDBURL)
	if err != nil {
		return nil, fmt.Errorf("connect game-state db (%s): %w", MaskDSN(r.cfg.GameDBURL), err)
	}
	return conn, nil
}

// MigrationRow is one row of the game-state schema_migrations table.
type MigrationRow struct {
	Version   int
	Name      string
	Checksum  string
	AppliedAt time.Time
}

// CheckMigrations validates a schema_migrations listing against the version the
// binary expects. Split out from the step so it is unit-testable without a
// database.
func CheckMigrations(rows []MigrationRow, want int) error {
	if len(rows) == 0 {
		return fmt.Errorf("schema_migrations is empty -- no migration has ever been applied " +
			"(the gameserver migrates at boot; did it fail or is this the wrong database?)")
	}
	for i, row := range rows {
		if row.Version != i+1 {
			return fmt.Errorf("schema_migrations version sequence has a gap: row %d is version %d, want %d",
				i, row.Version, i+1)
		}
		if row.Name == "" {
			return fmt.Errorf("migration %d has an empty name", row.Version)
		}
		if !strings.HasPrefix(row.Checksum, "sha256:") {
			return fmt.Errorf("migration %d (%s) checksum %q is not a sha256: digest -- "+
				"schema_migrations was hand-edited", row.Version, row.Name, row.Checksum)
		}
		if row.AppliedAt.IsZero() {
			return fmt.Errorf("migration %d (%s) has no applied_at timestamp", row.Version, row.Name)
		}
	}
	if got := rows[len(rows)-1].Version; got != want {
		return fmt.Errorf("game-state schema is at version %d, want %d -- "+
			"the deployed binary and the database disagree about the schema "+
			"(override with --expect-migration-version)", got, want)
	}
	return nil
}

// -------------------------------------------------------- gamestate_migrations

func (r *Runner) stepGameStateMigrations() (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), r.cfg.Timeout)
	defer cancel()
	conn, err := r.connectDB(ctx)
	if err != nil {
		return "", err
	}
	defer conn.Close(context.Background())

	rows, err := conn.Query(ctx,
		`SELECT version, name, checksum, applied_at FROM schema_migrations ORDER BY version`)
	if err != nil {
		return "", fmt.Errorf("query schema_migrations: %w "+
			"(table missing means the gameserver never migrated this database)", err)
	}
	defer rows.Close()

	var got []MigrationRow
	for rows.Next() {
		var m MigrationRow
		if err := rows.Scan(&m.Version, &m.Name, &m.Checksum, &m.AppliedAt); err != nil {
			return "", fmt.Errorf("scan schema_migrations row: %w", err)
		}
		got = append(got, m)
	}
	if err := rows.Err(); err != nil {
		return "", fmt.Errorf("read schema_migrations: %w", err)
	}
	if err := CheckMigrations(got, r.cfg.ExpectMigration); err != nil {
		return "", err
	}
	return fmt.Sprintf("version=%d (%s) applied=%s",
		got[len(got)-1].Version, got[len(got)-1].Name,
		got[len(got)-1].AppliedAt.UTC().Format(time.RFC3339)), nil
}

// -------------------------------------------------------- gamestate_player_row

// PlayerRow is the persisted player_states record.
type PlayerRow struct {
	UserID    string
	MapID     string
	X, Y      float32
	HP, MaxHP int
	UpdatedAt time.Time
}

// CheckPlayerRow validates a persisted row against what the realtime flow just
// did: the player walked +X from spawn on wantMap, took no damage, and the row
// was written after the run started. Pure, so its failure modes are unit-tested.
//
// maxX is the input count: movement is server-integrated (direction * speed *
// dt), so a single tick can never move a whole world unit -- an X at or beyond
// the input count means move_x is being treated as a displacement again.
func CheckPlayerRow(row PlayerRow, wantUser, wantMap string, maxX float32, notBefore time.Time) error {
	if row.UserID != wantUser {
		return fmt.Errorf("player_states row is for %q, want %q", row.UserID, wantUser)
	}
	if row.MapID != wantMap {
		return fmt.Errorf("player_states.map_id = %q, want %q", row.MapID, wantMap)
	}
	if row.X <= 0 {
		return fmt.Errorf("player_states.x = %.4f, want > 0 -- the persisted position is "+
			"the spawn point, so movement was never saved", row.X)
	}
	if row.X >= maxX {
		return fmt.Errorf("player_states.x = %.4f, want < %.1f (move_x must be a direction, "+
			"not a per-message displacement)", row.X, maxX)
	}
	if row.Y != 0 {
		return fmt.Errorf("player_states.y = %.4f, want 0 -- the smoke client only moved along +X", row.Y)
	}
	if row.HP <= 0 {
		return fmt.Errorf("player_states.hp = %d, want > 0", row.HP)
	}
	if row.MaxHP < row.HP {
		return fmt.Errorf("player_states.max_hp = %d < hp = %d", row.MaxHP, row.HP)
	}
	if row.UpdatedAt.Before(notBefore) {
		return fmt.Errorf("player_states.updated_at = %s is older than this smoke run (started %s) -- "+
			"a stale row survived, nothing was written now",
			row.UpdatedAt.UTC().Format(time.RFC3339Nano), notBefore.UTC().Format(time.RFC3339Nano))
	}
	return nil
}

// loadPlayerRow reads the player_states row for userID, or (zero, false) when
// the row does not exist yet.
func (r *Runner) loadPlayerRow(ctx context.Context, conn *pgx.Conn, userID string) (PlayerRow, bool, error) {
	var row PlayerRow
	err := conn.QueryRow(ctx,
		`SELECT user_id, map_id, x, y, hp, max_hp, updated_at FROM player_states WHERE user_id = $1`,
		userID,
	).Scan(&row.UserID, &row.MapID, &row.X, &row.Y, &row.HP, &row.MaxHP, &row.UpdatedAt)
	if err == pgx.ErrNoRows {
		return PlayerRow{}, false, nil
	}
	if err != nil {
		return PlayerRow{}, false, fmt.Errorf("query player_states: %w", err)
	}
	return row, true, nil
}

// stepGameStatePlayerRow polls player_states until the row this run should have
// produced appears, then asserts its contents.
//
// Polling rather than sleeping is required: the game server never writes on the
// hot path. A row lands either on the AsyncSaver's periodic sweep (30s) or when
// the reconnect hold expires and the entity leaves the world (another 30s), so
// the arrival time is anywhere in a ~60s window. A fixed sleep would be both
// slower on average and flaky at the tail.
func (r *Runner) stepGameStatePlayerRow() (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), r.cfg.DBPollTimeout+r.cfg.Timeout)
	defer cancel()
	conn, err := r.connectDB(ctx)
	if err != nil {
		return "", err
	}
	defer conn.Close(context.Background())

	deadline := time.Now().Add(r.cfg.DBPollTimeout)
	maxX := float32(r.cfg.Inputs)
	var (
		row    PlayerRow
		ok     bool
		polls  int
		lastEr error
	)
	for {
		polls++
		row, ok, lastEr = r.loadPlayerRow(ctx, conn, r.userID)
		if lastEr != nil {
			return "", lastEr
		}
		// Require both existence AND freshness before accepting: a user id is
		// unique per run so a stale row is impossible today, but --device-id
		// makes reruns share a user, and then an old row would pass instantly.
		if ok && !row.UpdatedAt.Before(r.runStart) {
			break
		}
		if time.Now().After(deadline) {
			if !ok {
				return "", fmt.Errorf("no player_states row for %s after %s (%d polls) -- "+
					"the game server never persisted this player (GAME_DB_URL unset on the "+
					"server? saver crashed? wrong database?)", r.userID, r.cfg.DBPollTimeout, polls)
			}
			return "", fmt.Errorf("player_states row for %s is stale after %s (%d polls): "+
				"updated_at=%s, run started %s", r.userID, r.cfg.DBPollTimeout, polls,
				row.UpdatedAt.UTC().Format(time.RFC3339Nano), r.runStart.UTC().Format(time.RFC3339Nano))
		}
		select {
		case <-ctx.Done():
			return "", fmt.Errorf("polling player_states: %w", ctx.Err())
		case <-time.After(r.cfg.DBPollInterval):
		}
	}

	if err := CheckPlayerRow(row, r.userID, r.cfg.MapID, maxX, r.runStart); err != nil {
		return "", err
	}
	r.persistedX = row.X
	r.persistedY = row.Y
	return fmt.Sprintf("map=%s x=%.4f y=%.4f hp=%d/%d updated_at=%s (%d polls)",
		row.MapID, row.X, row.Y, row.HP, row.MaxHP,
		row.UpdatedAt.UTC().Format(time.RFC3339), polls), nil
}

// MaskDSN redacts the password of a postgres DSN so it can appear in output.
func MaskDSN(dsn string) string {
	if dsn == "" {
		return "(unset)"
	}
	// postgres://user:password@host/db -> postgres://user:***@host/db
	scheme := ""
	rest := dsn
	if i := strings.Index(dsn, "://"); i >= 0 {
		scheme, rest = dsn[:i+3], dsn[i+3:]
	}
	at := strings.LastIndex(rest, "@")
	if at < 0 {
		return dsn
	}
	userinfo, host := rest[:at], rest[at:]
	if c := strings.Index(userinfo, ":"); c >= 0 {
		userinfo = userinfo[:c] + ":***"
	}
	return scheme + userinfo + host
}

// ---------------------------------------------------------- gamestate_reload

// stepGameStateReload proves the persisted row is actually READ BACK, not just
// written: it rejoins as the same user after the reconnect hold has expired and
// asserts the server spawns the entity at the persisted position instead of at
// the origin.
//
// Waiting out the hold is what makes the assertion mean anything. Inside the
// hold window the entity is still resident in the game server's memory and a
// reconnect reattaches to it (GameServer.cs: `existing != null` short-circuits
// the store load), so a passing check would prove nothing about the database.
// Only once the hold expires and the entity is evicted does the join path go
// through IPlayerStore.LoadPlayerAsync.
func (r *Runner) stepGameStateReload() (string, error) {
	if r.persistedX <= 0 {
		return "", fmt.Errorf("no persisted position from the previous step; cannot verify reload")
	}
	// Wait out the reconnect hold so the entity is evicted from memory.
	waited := time.Duration(0)
	if until := r.disconnectAt.Add(r.cfg.HoldTTL + holdGrace); time.Now().Before(until) {
		waited = time.Until(until)
		time.Sleep(waited)
	}

	// A fresh join token: tokens are single-use / short-lived, so redo the
	// gateway hop with the JWT we already hold.
	gwConn, err := r.dial(r.cfg.Transport, r.cfg.GatewayAddr)
	if err != nil {
		return "", fmt.Errorf("reload: %w", err)
	}
	var authResp messages.AuthResponse
	if err := r.roundTrip(gwConn, messages.MsgAuth, messages.AuthRequest{Token: r.gatewayJWT},
		messages.MsgAuthResp, &authResp); err != nil {
		gwConn.Close()
		return "", fmt.Errorf("reload: gateway auth: %w", err)
	}
	if !authResp.OK {
		gwConn.Close()
		return "", fmt.Errorf("reload: gateway auth rejected: %s", authResp.Error)
	}
	var enterResp messages.EnterWorldResponse
	if err := r.roundTrip(gwConn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: r.cfg.MapID},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		gwConn.Close()
		return "", fmt.Errorf("reload: enter world: %w", err)
	}
	gwConn.Close()
	if enterResp.Error != "" || enterResp.JoinToken == "" {
		return "", fmt.Errorf("reload: enter world gave no join token: %s", enterResp.Error)
	}

	conn, err := r.dial(transport.Normalize(enterResp.Transport), enterResp.ServerAddr)
	if err != nil {
		return "", fmt.Errorf("reload: %w", err)
	}
	defer conn.Close()

	var joinResp messages.JoinTokenResponse
	if err := r.roundTrip(conn, messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken},
		messages.MsgJoinTokenResp, &joinResp); err != nil {
		return "", fmt.Errorf("reload: join: %w", err)
	}
	if !joinResp.OK {
		return "", fmt.Errorf("reload: join rejected: %s", joinResp.Error)
	}

	// Read snapshots (without sending any input) until our entity shows up.
	state := messages.NewSnapshotState()
	deadline := time.Now().Add(r.cfg.Timeout)
	var spawn messages.EntitySnapshot
	found := false
	for time.Now().Before(deadline) && !found {
		env, err := r.recv(conn)
		if err != nil {
			break
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var s messages.SnapshotMessage
		if err := env.UnmarshalPayload(&s); err != nil {
			continue
		}
		if err := state.Apply(s); err != nil {
			return "", fmt.Errorf("reload: apply snapshot: %w", err)
		}
		if e, ok := state.Get(r.userID); ok {
			spawn, found = e, true
		}
	}
	if !found {
		return "", fmt.Errorf("reload: player %s never appeared in a snapshot after rejoin", r.userID)
	}

	if err := CheckReloadedPosition(spawn.X, spawn.Y, r.persistedX, r.persistedY, reloadTolerance); err != nil {
		return "", err
	}

	if env, err := messages.NewEnvelope(messages.MsgDisconnect, struct{}{}); err == nil {
		_ = r.send(conn, env)
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Sprintf("respawned at x=%.4f y=%.4f from persisted x=%.4f y=%.4f (waited %s for hold)",
		spawn.X, spawn.Y, r.persistedX, r.persistedY, waited.Round(time.Second)), nil
}

// CheckReloadedPosition asserts a rejoining player was restored to the position
// PostgreSQL holds rather than respawned at the origin.
func CheckReloadedPosition(gotX, gotY, wantX, wantY, tol float32) error {
	if gotX == 0 && gotY == 0 {
		return fmt.Errorf("rejoined player spawned at the origin, but PostgreSQL holds "+
			"x=%.4f y=%.4f -- the saved state was NOT loaded on join", wantX, wantY)
	}
	if abs32(gotX-wantX) > tol || abs32(gotY-wantY) > tol {
		return fmt.Errorf("rejoined player is at x=%.4f y=%.4f, but PostgreSQL holds "+
			"x=%.4f y=%.4f (tolerance %.4f) -- the restored position does not match the "+
			"persisted one", gotX, gotY, wantX, wantY, tol)
	}
	return nil
}

func abs32(f float32) float32 {
	if f < 0 {
		return -f
	}
	return f
}
