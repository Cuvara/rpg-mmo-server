package registry

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

// RegistryService wraps a ServerRegistry for game server lookup.
type RegistryService struct {
	reg storage.ServerRegistry
}

// NewRegistryService creates a RegistryService backed by the given registry.
func NewRegistryService(reg storage.ServerRegistry) *RegistryService {
	return &RegistryService{reg: reg}
}

// FindServer locates a server for the given mapID that has available capacity
// (PlayerCount < Capacity). Returns ErrNoServer if none found.
func (s *RegistryService) FindServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	servers, err := s.reg.FindByMapID(ctx, mapID)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("find servers: %w", err)
	}
	for _, srv := range servers {
		if srv.PlayerCount < srv.Capacity {
			return srv, nil
		}
	}
	return storage.ServerInfo{}, fmt.Errorf("no available server for map %s", mapID)
}

// RegisterServer registers a game server in the registry.
func (s *RegistryService) RegisterServer(ctx context.Context, info storage.ServerInfo) error {
	return s.reg.Register(ctx, info)
}

// DeregisterServer removes a game server from the registry.
func (s *RegistryService) DeregisterServer(ctx context.Context, serverID string) error {
	return s.reg.Deregister(ctx, serverID)
}
