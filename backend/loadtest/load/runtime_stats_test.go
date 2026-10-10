package load

import (
	"testing"
	"time"
)

// TestAggregateServer_RuntimeAndKCP pins the game-server runtime-cost and KCP
// listener fields a load run reports: GC per generation, pause share, CPU cores,
// allocation rate and the KCP session/datagram counters summed over reasons.
func TestAggregateServer_RuntimeAndKCP(t *testing.T) {
	t0 := time.Unix(0, 0)
	before := scrapeText(`
gameserver_gc_collections_total{map_id="m",generation="0"} 10
gameserver_gc_collections_total{map_id="m",generation="1"} 2
gameserver_gc_collections_total{map_id="m",generation="2"} 1
gameserver_gc_pause_seconds_total{map_id="m"} 0.5
gameserver_gc_allocated_bytes_total{map_id="m"} 1000
gameserver_process_cpu_seconds_total{map_id="m"} 100
gameserver_kcp_sessions_created_total{map_id="m"} 5
gameserver_kcp_sessions_rejected_total{map_id="m",reason="per_ip_cap"} 0
gameserver_kcp_sessions_rejected_total{map_id="m",reason="rate"} 0
gameserver_kcp_datagrams_dropped_total{map_id="m",reason="bad_crypto"} 1
gameserver_kcp_receive_busy_seconds_total{map_id="m"} 1
gameserver_kcp_datagrams_received_total{map_id="m"} 10000
`, t0)
	after := scrapeText(`
gameserver_gc_collections_total{map_id="m",generation="0"} 40
gameserver_gc_collections_total{map_id="m",generation="1"} 4
gameserver_gc_collections_total{map_id="m",generation="2"} 1
gameserver_gc_pause_seconds_total{map_id="m"} 1.5
gameserver_gc_allocated_bytes_total{map_id="m"} 11000
gameserver_process_cpu_seconds_total{map_id="m"} 120
gameserver_process_working_set_bytes{map_id="m"} 123456
gameserver_kcp_sessions{map_id="m"} 17
gameserver_kcp_sessions_created_total{map_id="m"} 22
gameserver_kcp_sessions_rejected_total{map_id="m",reason="per_ip_cap"} 3
gameserver_kcp_sessions_rejected_total{map_id="m",reason="rate"} 1
gameserver_kcp_datagrams_dropped_total{map_id="m",reason="bad_crypto"} 4
gameserver_kcp_receive_busy_seconds_total{map_id="m"} 5
gameserver_kcp_datagrams_received_total{map_id="m"} 30000
`, t0.Add(10*time.Second))

	s := aggregateServer(before, after, nil, nil, 10, 1.0/60, nil, nil)

	if s.GCGen0 != 30 || s.GCGen1 != 2 || s.GCGen2 != 0 {
		t.Errorf("gc gens = %v/%v/%v, want 30/2/0", s.GCGen0, s.GCGen1, s.GCGen2)
	}
	if s.GCPauseSec != 1 || s.GCPauseRatio != 0.1 {
		t.Errorf("gc pause = %v s, ratio %v; want 1 s, 0.1", s.GCPauseSec, s.GCPauseRatio)
	}
	if s.CPUCores != 2 || s.AllocBytesPerSec != 1000 || s.WorkingSetBytes != 123456 {
		t.Errorf("cpu %v cores, alloc %v B/s, ws %v", s.CPUCores, s.AllocBytesPerSec, s.WorkingSetBytes)
	}
	if s.KCPSessionsLive != 17 || s.KCPSessionsCreated != 17 || s.KCPSessionsRejected != 4 ||
		s.KCPRejectedPerIPCap != 3 || s.KCPDatagramsDropped != 3 {
		t.Errorf("kcp = %+v", s)
	}
	if s.KCPReceiveBusyRatio != 0.4 || s.KCPDatagramsInPerSec != 2000 {
		t.Errorf("kcp receive busy %v, in %v/s; want 0.4, 2000", s.KCPReceiveBusyRatio, s.KCPDatagramsInPerSec)
	}
}
