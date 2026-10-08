package load

import kcp "github.com/xtaci/kcp-go/v5"

// KCPStats is what the virtual clients' KCP stack observed over the measurement
// window: the transport-level half of a run that the application counters
// (snapshot cadence, ack latency) cannot see.
//
// It is read from kcp-go's process-wide DefaultSnmp, so it aggregates every
// gameplay session this load generator holds. The gateway hop is TCP and never
// touches these counters. Counters are deltas over the window; the queue fields
// are gauges sampled at the end of it.
type KCPStats struct {
	OutSegs uint64 `json:"out_segs"`
	InSegs  uint64 `json:"in_segs"`
	OutPkts uint64 `json:"out_pkts"`
	InPkts  uint64 `json:"in_pkts"`
	// OutBytes / InBytes are UDP payload bytes, i.e. including KCP headers and
	// retransmissions — the real wire cost, unlike the application byte counters.
	OutBytes uint64 `json:"out_bytes"`
	InBytes  uint64 `json:"in_bytes"`

	RetransSegs      uint64 `json:"retrans_segs"`
	FastRetransSegs  uint64 `json:"fast_retrans_segs"`
	EarlyRetransSegs uint64 `json:"early_retrans_segs"`
	// LostSegs counts segments KCP inferred as lost (RTO expiry).
	LostSegs uint64 `json:"lost_segs"`
	// RepeatSegs counts duplicate segments received.
	RepeatSegs uint64 `json:"repeat_segs"`
	// InErrs, InCsumErrors and KCPInErrors are datagrams the client dropped:
	// socket errors, crypto checksum failures (key mismatch) and malformed KCP.
	InErrs       uint64 `json:"in_errs"`
	InCsumErrors uint64 `json:"in_csum_errors"`
	KCPInErrors  uint64 `json:"kcp_in_errors"`

	// RetransRatio is RetransSegs / OutSegs, LossRatio LostSegs / OutSegs.
	RetransRatio float64 `json:"retrans_ratio"`
	LossRatio    float64 `json:"loss_ratio"`

	// End-of-window queue depths across all sessions (segments).
	SndQueue  uint64 `json:"snd_queue"`
	SndBuffer uint64 `json:"snd_buffer"`
	RcvQueue  uint64 `json:"rcv_queue"`
}

// snapshotKCP copies kcp-go's process-wide counters.
func snapshotKCP() *kcp.Snmp { return kcp.DefaultSnmp.Copy() }

// kcpDelta derives the window's KCPStats from two snapshots.
func kcpDelta(before, after *kcp.Snmp) KCPStats {
	if before == nil || after == nil {
		return KCPStats{}
	}
	d := func(a, b uint64) uint64 {
		if a < b {
			return 0
		}
		return a - b
	}
	s := KCPStats{
		OutSegs:          d(after.OutSegs, before.OutSegs),
		InSegs:           d(after.InSegs, before.InSegs),
		OutPkts:          d(after.OutPkts, before.OutPkts),
		InPkts:           d(after.InPkts, before.InPkts),
		OutBytes:         d(after.OutBytes, before.OutBytes),
		InBytes:          d(after.InBytes, before.InBytes),
		RetransSegs:      d(after.RetransSegs, before.RetransSegs),
		FastRetransSegs:  d(after.FastRetransSegs, before.FastRetransSegs),
		EarlyRetransSegs: d(after.EarlyRetransSegs, before.EarlyRetransSegs),
		LostSegs:         d(after.LostSegs, before.LostSegs),
		RepeatSegs:       d(after.RepeatSegs, before.RepeatSegs),
		InErrs:           d(after.InErrs, before.InErrs),
		InCsumErrors:     d(after.InCsumErrors, before.InCsumErrors),
		KCPInErrors:      d(after.KCPInErrors, before.KCPInErrors),
		SndQueue:         after.RingBufferSndQueue,
		SndBuffer:        after.RingBufferSndBuffer,
		RcvQueue:         after.RingBufferRcvQueue,
	}
	if s.OutSegs > 0 {
		s.RetransRatio = float64(s.RetransSegs) / float64(s.OutSegs)
		s.LossRatio = float64(s.LostSegs) / float64(s.OutSegs)
	}
	return s
}
