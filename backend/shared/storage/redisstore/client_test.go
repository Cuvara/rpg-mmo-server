package redisstore

import (
	"context"
	"errors"
	"net"
	"testing"

	"github.com/redis/go-redis/v9"
	"time"
)

// TestClientOptionsDefaults asserts every zero field is filled from the
// Default* constants. This is the G5 regression guard: the pre-hardening client
// was built with only Addr/Password, so every call inherited go-redis defaults
// and could hang a request path.
func TestClientOptionsDefaults(t *testing.T) {
	got := ClientOptions{Addr: "localhost:6379"}.redisOptions()

	tests := []struct {
		name string
		got  any
		want any
	}{
		{"DialTimeout", got.DialTimeout, DefaultDialTimeout},
		{"ReadTimeout", got.ReadTimeout, DefaultReadTimeout},
		{"WriteTimeout", got.WriteTimeout, DefaultWriteTimeout},
		{"MaxRetries", got.MaxRetries, DefaultMaxRetries},
		{"MinRetryBackoff", got.MinRetryBackoff, DefaultMinRetryBackoff},
		{"MaxRetryBackoff", got.MaxRetryBackoff, DefaultMaxRetryBackoff},
		{"PoolSize", got.PoolSize, DefaultPoolSize},
		{"MinIdleConns", got.MinIdleConns, DefaultMinIdleConns},
		{"PoolTimeout", got.PoolTimeout, DefaultPoolTimeout},
		{"ConnMaxIdleTime", got.ConnMaxIdleTime, DefaultConnMaxIdleTime},
	}
	for _, tt := range tests {
		if tt.got != tt.want {
			t.Errorf("%s = %v, want %v", tt.name, tt.got, tt.want)
		}
	}
}

// TestClientOptionsOverrides asserts an explicitly set field survives, and that
// a negative value (which go-redis reads as "disable") is not treated as unset.
func TestClientOptionsOverrides(t *testing.T) {
	opts := ClientOptions{
		Addr:         "h:1",
		DialTimeout:  7 * time.Second,
		ReadTimeout:  -1,
		PoolSize:     3,
		MaxRetries:   -1,
		MinIdleConns: 2,
	}.redisOptions()

	if opts.DialTimeout != 7*time.Second {
		t.Errorf("DialTimeout = %v, want 7s", opts.DialTimeout)
	}
	if opts.ReadTimeout != -1 {
		t.Errorf("ReadTimeout = %v, want -1 (disabled, not defaulted)", opts.ReadTimeout)
	}
	if opts.MaxRetries != -1 {
		t.Errorf("MaxRetries = %v, want -1 (disabled, not defaulted)", opts.MaxRetries)
	}
	if opts.PoolSize != 3 {
		t.Errorf("PoolSize = %v, want 3", opts.PoolSize)
	}
	if opts.MinIdleConns != 2 {
		t.Errorf("MinIdleConns = %v, want 2", opts.MinIdleConns)
	}
}

// TestNewEventStreamWidensReadTimeout guards the interaction between the client
// defaults and blocking reads: a read timeout at or below the XREADGROUP block
// duration would make every idle poll look like an i/o timeout.
func TestNewEventStreamWidensReadTimeout(t *testing.T) {
	s := NewEventStream("localhost:6379", "", "g", "c")
	defer func() { _ = s.Close() }()

	client, ok := s.client.(*redis.Client)
	if !ok {
		t.Fatalf("client type = %T, want *redis.Client", s.client)
	}
	if got := client.Options().ReadTimeout; got <= defaultStreamBlock {
		t.Errorf("ReadTimeout = %v, must exceed block %v", got, defaultStreamBlock)
	}
}

// TestPingBoundedByTimeout proves the health probe fails fast against an
// unreachable address instead of inheriting a long default. A readiness handler
// that blocks longer than the probe interval piles up handlers.
func TestPingBoundedByTimeout(t *testing.T) {
	// A port nothing listens on: connections are refused or time out, never
	// succeed.
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr := ln.Addr().String()
	_ = ln.Close() // now closed: nothing accepts here

	client := NewRedisClient(addr, "")
	defer func() { _ = client.Close() }()

	start := time.Now()
	err = Ping(context.Background(), client, 500*time.Millisecond)
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("Ping() to a dead address returned nil, want error")
	}
	// Generous ceiling: the point is that it is bounded, not that it is exact.
	if elapsed > 5*time.Second {
		t.Errorf("Ping() took %v, want it bounded well under 5s", elapsed)
	}
}

// TestPingNilClient guards the probe against a nil client (memory backend).
func TestPingNilClient(t *testing.T) {
	if err := Ping(context.Background(), nil, time.Second); err == nil {
		t.Error("Ping(nil) = nil, want error")
	}
}

// TestIsNoGroup is the classifier behind the G4 recovery: NOGROUP is a
// permanent condition needing re-creation, everything else is transient.
func TestIsNoGroup(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want bool
	}{
		{"nil", nil, false},
		{"nogroup", errors.New("NOGROUP No such consumer group 'gateway'"), true},
		{"busygroup", errors.New("BUSYGROUP Consumer Group name already exists"), false},
		{"connection refused", errors.New("dial tcp: connect: connection refused"), false},
		{"loading", errors.New("LOADING Redis is loading the dataset in memory"), false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := isNoGroup(tt.err); got != tt.want {
				t.Errorf("isNoGroup(%v) = %v, want %v", tt.err, got, tt.want)
			}
		})
	}
}
