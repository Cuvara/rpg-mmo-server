package load

import (
	"testing"

	kcp "github.com/xtaci/kcp-go/v5"
)

func TestKCPDelta(t *testing.T) {
	before := &kcp.Snmp{OutSegs: 100, RetransSegs: 1, LostSegs: 0, InSegs: 50}
	after := &kcp.Snmp{OutSegs: 1100, RetransSegs: 21, LostSegs: 5, InSegs: 950, RingBufferSndQueue: 3}
	got := kcpDelta(before, after)
	if got.OutSegs != 1000 || got.RetransSegs != 20 || got.LostSegs != 5 || got.InSegs != 900 {
		t.Fatalf("delta = %+v", got)
	}
	if got.RetransRatio != 0.02 || got.LossRatio != 0.005 {
		t.Errorf("ratios = %v / %v, want 0.02 / 0.005", got.RetransRatio, got.LossRatio)
	}
	if got.SndQueue != 3 {
		t.Errorf("SndQueue = %d, want the end-of-window gauge 3", got.SndQueue)
	}
	if z := kcpDelta(nil, after); z != (KCPStats{}) {
		t.Errorf("nil snapshot must yield zero stats, got %+v", z)
	}
}
