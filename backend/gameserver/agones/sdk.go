package agones

import (
	"log/slog"
	"time"

	agonessdk "agones.dev/agones/sdks/go"
)

// SDK is the interface for Agones game server SDK operations.
// Mockable for testing without a real Agones sidecar.
type SDK interface {
	// Ready marks the game server as ready to receive connections.
	Ready() error
	// Shutdown marks the game server for shutdown.
	Shutdown() error
	// Allocate self-marks the game server as allocated.
	Allocate() error
	// Health sends a health ping to the Agones sidecar.
	Health() error
}

// RealSDK wraps the actual Agones Go SDK.
type RealSDK struct {
	sdk    *agonessdk.SDK
	logger *slog.Logger
}

// NewRealSDK creates and connects to the Agones sidecar.
// Blocks until connected or timeout (30s default).
func NewRealSDK(logger *slog.Logger) (*RealSDK, error) {
	s, err := agonessdk.NewSDK()
	if err != nil {
		return nil, err
	}
	return &RealSDK{sdk: s, logger: logger}, nil
}

func (r *RealSDK) Ready() error {
	r.logger.Info("agones: marking Ready")
	return r.sdk.Ready()
}

func (r *RealSDK) Shutdown() error {
	r.logger.Info("agones: marking Shutdown")
	return r.sdk.Shutdown()
}

func (r *RealSDK) Allocate() error {
	r.logger.Info("agones: self-Allocate")
	return r.sdk.Allocate()
}

func (r *RealSDK) Health() error {
	return r.sdk.Health()
}

// StartHealthLoop sends health pings every interval until stopCh is closed.
func StartHealthLoop(sdk SDK, interval time.Duration, stopCh <-chan struct{}, logger *slog.Logger) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			if err := sdk.Health(); err != nil {
				logger.Error("agones health ping failed", "err", err)
			}
		case <-stopCh:
			return
		}
	}
}

// NoopSDK is a no-op implementation for running without Agones.
type NoopSDK struct {
	logger *slog.Logger
}

// NewNoopSDK creates a no-op SDK for local dev / testing.
func NewNoopSDK(logger *slog.Logger) *NoopSDK {
	return &NoopSDK{logger: logger}
}

func (n *NoopSDK) Ready() error {
	n.logger.Debug("agones(noop): Ready")
	return nil
}

func (n *NoopSDK) Shutdown() error {
	n.logger.Debug("agones(noop): Shutdown")
	return nil
}

func (n *NoopSDK) Allocate() error {
	n.logger.Debug("agones(noop): Allocate")
	return nil
}

func (n *NoopSDK) Health() error {
	return nil
}
