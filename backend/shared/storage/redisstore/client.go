package redisstore

import (
	"context"
	"fmt"
	"time"

	"github.com/redis/go-redis/v9"
)

// Timeout, retry and pool defaults for every Redis client this package builds.
//
// The point of setting these explicitly is that go-redis' own defaults are not
// suitable for a request path: ReadTimeout/WriteTimeout default to 3s (fine) but
// DialTimeout is 5s and MaxRetries is 3 with no ceiling on how long the retry
// sequence takes, so a single Get against a black-holed Redis could occupy a
// caller for tens of seconds. Bounding all three keeps a Redis outage a fast
// failure instead of a pile-up of stuck goroutines.
//
// The numbers are chosen against SessionTTL (minutes) and the gateway's own
// per-request expectations (auth p99 < 100ms), so a healthy Redis never comes
// close to them and an unhealthy one is detected in ~1s rather than ~30s.
const (
	// DefaultDialTimeout bounds establishing a new connection.
	DefaultDialTimeout = 2 * time.Second
	// DefaultReadTimeout bounds a single command read. Must stay above the
	// stream block timeout, otherwise a blocking XREADGROUP looks like a
	// timeout every iteration — see NewRedisClientWithOptions.
	DefaultReadTimeout = 2 * time.Second
	// DefaultWriteTimeout bounds a single command write.
	DefaultWriteTimeout = 2 * time.Second

	// DefaultMaxRetries is how many times go-redis retries a failed command
	// before returning the error. Bounded, with backoff, so a Redis blip is
	// absorbed but a Redis outage surfaces quickly.
	DefaultMaxRetries = 3
	// DefaultMinRetryBackoff / DefaultMaxRetryBackoff bracket the exponential
	// backoff between those retries.
	DefaultMinRetryBackoff = 16 * time.Millisecond
	DefaultMaxRetryBackoff = 256 * time.Millisecond

	// DefaultPoolSize caps concurrent connections. The gateway is I/O bound on
	// Redis during login bursts; an unbounded pool would let a login storm open
	// a connection per goroutine and exhaust Redis' own client limit.
	DefaultPoolSize = 32
	// DefaultMinIdleConns keeps warm connections so a login burst does not pay
	// dial latency on every request.
	DefaultMinIdleConns = 4
	// DefaultPoolTimeout bounds how long a caller waits for a free connection
	// when the pool is saturated, instead of blocking indefinitely.
	DefaultPoolTimeout = 3 * time.Second
	// DefaultConnMaxIdleTime recycles idle connections so a silently dropped
	// TCP connection (NAT/firewall idle reap) is not handed to a caller.
	DefaultConnMaxIdleTime = 5 * time.Minute
)

// ClientOptions is the tunable surface for NewRedisClientWithOptions. The zero
// value is valid: every unset field falls back to the Default* constant above,
// so callers override only what they care about.
type ClientOptions struct {
	Addr     string
	Password string

	DialTimeout  time.Duration
	ReadTimeout  time.Duration
	WriteTimeout time.Duration

	MaxRetries      int
	MinRetryBackoff time.Duration
	MaxRetryBackoff time.Duration

	PoolSize        int
	MinIdleConns    int
	PoolTimeout     time.Duration
	ConnMaxIdleTime time.Duration
}

// withDefaults returns a copy with every zero field replaced by its default.
// A negative value is meaningful to go-redis (it disables the feature), so only
// exact zeros are substituted.
func (o ClientOptions) withDefaults() ClientOptions {
	if o.DialTimeout == 0 {
		o.DialTimeout = DefaultDialTimeout
	}
	if o.ReadTimeout == 0 {
		o.ReadTimeout = DefaultReadTimeout
	}
	if o.WriteTimeout == 0 {
		o.WriteTimeout = DefaultWriteTimeout
	}
	if o.MaxRetries == 0 {
		o.MaxRetries = DefaultMaxRetries
	}
	if o.MinRetryBackoff == 0 {
		o.MinRetryBackoff = DefaultMinRetryBackoff
	}
	if o.MaxRetryBackoff == 0 {
		o.MaxRetryBackoff = DefaultMaxRetryBackoff
	}
	if o.PoolSize == 0 {
		o.PoolSize = DefaultPoolSize
	}
	if o.MinIdleConns == 0 {
		o.MinIdleConns = DefaultMinIdleConns
	}
	if o.PoolTimeout == 0 {
		o.PoolTimeout = DefaultPoolTimeout
	}
	if o.ConnMaxIdleTime == 0 {
		o.ConnMaxIdleTime = DefaultConnMaxIdleTime
	}
	return o
}

// redisOptions converts to the go-redis option struct.
func (o ClientOptions) redisOptions() *redis.Options {
	o = o.withDefaults()
	return &redis.Options{
		Addr:            o.Addr,
		Password:        o.Password,
		DialTimeout:     o.DialTimeout,
		ReadTimeout:     o.ReadTimeout,
		WriteTimeout:    o.WriteTimeout,
		MaxRetries:      o.MaxRetries,
		MinRetryBackoff: o.MinRetryBackoff,
		MaxRetryBackoff: o.MaxRetryBackoff,
		PoolSize:        o.PoolSize,
		MinIdleConns:    o.MinIdleConns,
		PoolTimeout:     o.PoolTimeout,
		ConnMaxIdleTime: o.ConnMaxIdleTime,
	}
}

// NewRedisClient builds a go-redis client from the shared config fields
// (config.Config.RedisAddr / RedisPassword) with the package defaults for
// timeouts, retries and pooling.
func NewRedisClient(addr, password string) *redis.Client {
	return NewRedisClientWithOptions(ClientOptions{Addr: addr, Password: password})
}

// NewRedisClientWithOptions builds a go-redis client, filling any zero field in
// opts from the Default* constants.
//
// Note on blocking reads: a client used for blocking commands (XREADGROUP with
// Block) must have ReadTimeout greater than the block duration, or every block
// looks like an i/o timeout. EventStream handles this itself by widening the
// read timeout — see NewEventStream.
func NewRedisClientWithOptions(opts ClientOptions) *redis.Client {
	return redis.NewClient(opts.redisOptions())
}

// Ping checks Redis liveness with a bounded timeout, independent of whatever
// deadline the caller's context carries. Used by health/readiness probes, which
// must never block a probe handler for longer than the probe interval.
func Ping(ctx context.Context, client redis.UniversalClient, timeout time.Duration) error {
	if client == nil {
		return fmt.Errorf("redis ping: nil client")
	}
	if timeout <= 0 {
		timeout = DefaultDialTimeout
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	if err := client.Ping(ctx).Err(); err != nil {
		return fmt.Errorf("redis ping: %w", err)
	}
	return nil
}
