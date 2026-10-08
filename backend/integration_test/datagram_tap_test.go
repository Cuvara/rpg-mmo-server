//go:build integration

package integration

import (
	"encoding/binary"
	"net"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"testing"
	"time"
)

// datagramTap is the gameplay-hop counterpart of byteTap. The gameplay hop is
// KCP over UDP, so a TCP relay cannot sit in it; this relays datagrams instead
// and records, per direction, what a passive observer can read off them.
//
// What is recorded is the KCP stream as an observer would reassemble it: the
// payload of every PUSH segment, de-duplicated by sequence number and put back
// in sequence order. That is exactly the byte stream the application framing
// rides on, so the same credential scan that runs over a TCP capture runs over
// this one, and a credential that crossed the hop in the clear is found even
// when KCP split it across segments. The tap holds no key: with TRANSPORT_KEY
// set the datagrams are ciphertext, no segment parses, and nothing is
// recorded, which is the honest answer for that configuration.
type datagramTap struct {
	label    string
	upstream *net.UDPAddr
	ln       *net.UDPConn

	mu    sync.Mutex
	peers map[string]*net.UDPConn
	c2s   map[uint32][]byte // sn -> payload
	s2c   map[uint32][]byte
	raw   int
}

// kcpCmdPush is IKCP_CMD_PUSH, the data-carrying segment.
const kcpCmdPush = 81

// kcpOverhead is the KCP segment header size.
const kcpOverhead = 24

func startDatagramTap(t *testing.T, label, upstream string) *datagramTap {
	t.Helper()
	up, err := net.ResolveUDPAddr("udp", upstream)
	if err != nil {
		t.Fatalf("tap %s resolve upstream %s: %v", label, upstream, err)
	}
	ln, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("tap %s listen: %v", label, err)
	}
	tp := &datagramTap{
		label: label, upstream: up, ln: ln,
		peers: map[string]*net.UDPConn{},
		c2s:   map[uint32][]byte{}, s2c: map[uint32][]byte{},
	}
	go tp.downLoop()
	t.Cleanup(tp.close)
	return tp
}

func (tp *datagramTap) addr() string { return tp.ln.LocalAddr().String() }

func (tp *datagramTap) close() {
	_ = tp.ln.Close()
	tp.mu.Lock()
	defer tp.mu.Unlock()
	for _, c := range tp.peers {
		_ = c.Close()
	}
}

// downLoop relays client datagrams upstream, one upstream socket per client
// endpoint so the server sees a distinct peer for each.
func (tp *datagramTap) downLoop() {
	buf := make([]byte, 64*1024)
	for {
		n, client, err := tp.ln.ReadFromUDP(buf)
		if err != nil {
			return
		}
		pkt := append([]byte(nil), buf[:n]...)
		tp.recordDatagram(pkt, tp.c2s)

		tp.mu.Lock()
		up, ok := tp.peers[client.String()]
		if !ok {
			up, err = net.DialUDP("udp", nil, tp.upstream)
			if err == nil {
				tp.peers[client.String()] = up
				go tp.upLoop(up, client)
			}
		}
		tp.mu.Unlock()
		if up != nil {
			_, _ = up.Write(pkt)
		}
	}
}

func (tp *datagramTap) upLoop(up *net.UDPConn, client *net.UDPAddr) {
	buf := make([]byte, 64*1024)
	for {
		_ = up.SetReadDeadline(time.Now().Add(2 * time.Minute))
		n, err := up.Read(buf)
		if err != nil {
			return
		}
		pkt := append([]byte(nil), buf[:n]...)
		tp.recordDatagram(pkt, tp.s2c)
		_, _ = tp.ln.WriteToUDP(pkt, client)
	}
}

// recordDatagram walks the KCP segments in one datagram and keeps each PUSH
// payload once, keyed by sequence number (retransmissions are duplicates).
func (tp *datagramTap) recordDatagram(pkt []byte, into map[uint32][]byte) {
	tp.mu.Lock()
	defer tp.mu.Unlock()
	tp.raw++
	for len(pkt) >= kcpOverhead {
		cmd := pkt[4]
		sn := binary.LittleEndian.Uint32(pkt[12:16])
		length := int(binary.LittleEndian.Uint32(pkt[20:24]))
		if length < 0 || kcpOverhead+length > len(pkt) {
			return // not KCP this tap can parse (e.g. encrypted): record nothing
		}
		if cmd == kcpCmdPush {
			if _, seen := into[sn]; !seen {
				into[sn] = append([]byte(nil), pkt[kcpOverhead:kcpOverhead+length]...)
			}
		}
		pkt = pkt[kcpOverhead+length:]
	}
}

func orderedBySN(m map[uint32][]byte) []byte {
	keys := make([]uint32, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })
	var out []byte
	for _, k := range keys {
		out = append(out, m[k]...)
	}
	return out
}

// snapshot mirrors byteTap.snapshot: the reassembled stream per direction and
// their concatenation.
func (tp *datagramTap) snapshot() (all []byte, perDir map[string][]byte) {
	tp.mu.Lock()
	defer tp.mu.Unlock()
	c2s, s2c := orderedBySN(tp.c2s), orderedBySN(tp.s2c)
	perDir = map[string][]byte{"c2s": c2s, "s2c": s2c}
	all = append(append(all, c2s...), s2c...)
	return all, perDir
}

// datagramCount reports how many datagrams crossed the tap, so a caller can
// prove the hop really ran through it.
func (tp *datagramTap) datagramCount() int {
	tp.mu.Lock()
	defer tp.mu.Unlock()
	return tp.raw
}

func (tp *datagramTap) writeCapture(t *testing.T, dir string) string {
	t.Helper()
	all, per := tp.snapshot()
	path := filepath.Join(dir, tp.label+".bin")
	if err := os.WriteFile(path, all, 0o644); err != nil {
		t.Fatalf("write capture: %v", err)
	}
	for d, b := range per {
		p := filepath.Join(dir, tp.label+"."+d+".bin")
		if err := os.WriteFile(p, b, 0o644); err != nil {
			t.Fatalf("write capture %s: %v", d, err)
		}
	}
	return path
}
