package registry

import (
	"context"

	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// Allocator requests new game server instances (e.g., via Agones).
type Allocator interface {
	// AllocateServer requests a new game server for the given map.
	AllocateServer(ctx context.Context, mapID string) (storage.ServerInfo, error)
}

// StubAllocator is a placeholder that always returns ErrNotImplemented.
type StubAllocator struct{}

// AllocateServer returns ErrNotImplemented.
func (a *StubAllocator) AllocateServer(_ context.Context, _ string) (storage.ServerInfo, error) {
	return storage.ServerInfo{}, gameerrors.New(gameerrors.ErrNotImplemented, "allocator not implemented")
}
