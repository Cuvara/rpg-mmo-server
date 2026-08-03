package agones

import (
	"log/slog"
	"sync/atomic"
	"testing"
	"time"
)

func TestNoopSDK(t *testing.T) {
	sdk := NewNoopSDK(slog.Default())

	if err := sdk.Ready(); err != nil {
		t.Errorf("Ready() error: %v", err)
	}
	if err := sdk.Health(); err != nil {
		t.Errorf("Health() error: %v", err)
	}
	if err := sdk.Allocate(); err != nil {
		t.Errorf("Allocate() error: %v", err)
	}
	if err := sdk.Shutdown(); err != nil {
		t.Errorf("Shutdown() error: %v", err)
	}
}

func TestNoopSDK_ImplementsInterface(t *testing.T) {
	var _ SDK = (*NoopSDK)(nil)
	var _ SDK = (*RealSDK)(nil)
}

func TestStartHealthLoop(t *testing.T) {
	sdk := NewNoopSDK(slog.Default())
	stopCh := make(chan struct{})

	var healthCalls atomic.Int64
	mockSDK := &countingSDK{inner: sdk, healthCount: &healthCalls}

	go StartHealthLoop(mockSDK, 10*time.Millisecond, stopCh, slog.Default())

	time.Sleep(55 * time.Millisecond)
	close(stopCh)
	time.Sleep(15 * time.Millisecond)

	count := healthCalls.Load()
	if count < 3 {
		t.Errorf("expected >= 3 health calls, got %d", count)
	}
}

// countingSDK counts Health() calls for testing.
type countingSDK struct {
	inner       SDK
	healthCount *atomic.Int64
}

func (c *countingSDK) Ready() error    { return c.inner.Ready() }
func (c *countingSDK) Shutdown() error { return c.inner.Shutdown() }
func (c *countingSDK) Allocate() error { return c.inner.Allocate() }
func (c *countingSDK) Health() error {
	c.healthCount.Add(1)
	return c.inner.Health()
}
