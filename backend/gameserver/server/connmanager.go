package server

import "sync"

// ConnectionManager tracks active player connections.
type ConnectionManager struct {
	mu    sync.RWMutex
	conns map[string]*Connection
}

// NewConnectionManager creates a connection manager.
func NewConnectionManager() *ConnectionManager {
	return &ConnectionManager{conns: make(map[string]*Connection)}
}

// Add registers a connection.
func (m *ConnectionManager) Add(conn *Connection) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.conns[conn.UserID] = conn
}

// Remove unregisters a connection.
func (m *ConnectionManager) Remove(userID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.conns, userID)
}

// Get returns a connection by user ID.
func (m *ConnectionManager) Get(userID string) *Connection {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.conns[userID]
}

// ForEach calls fn for each active connection.
func (m *ConnectionManager) ForEach(fn func(conn *Connection)) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	for _, conn := range m.conns {
		fn(conn)
	}
}

// Count returns the number of active connections.
func (m *ConnectionManager) Count() int {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return len(m.conns)
}

// CloseAll closes all connections.
func (m *ConnectionManager) CloseAll() {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, conn := range m.conns {
		conn.Close()
	}
	m.conns = make(map[string]*Connection)
}
