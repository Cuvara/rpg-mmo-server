package smoke

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestNoJSONDefaultingEnvelopeConstructor is a source-level guard, and it exists
// because this exact defect was missed twice by two different reviewers on the
// same day.
//
// messages.NewEnvelope is a convenience constructor that hard-codes
// EncodingJSON. Every frame this binary sends must go through NewEnvelopeAs with
// the runner's resolved encoding instead, or that frame is JSON regardless of
// -encoding. On a sealed session the server rejects it.
//
// Why a source scan rather than a behavioural test: the miss was not a wrong
// behaviour, it was an UNVISITED LINE. The `-encoding` change fixed the four call
// sites in runner.go and left the fifth in db.go, on the reload check — the only
// step that sends a frame from that file, reached only when the game-state
// database checks run. A behavioural test would have to reach that step to
// notice, which is exactly why nobody did. This test needs no server, no
// database, and no network, and it fails on the next site somebody adds.
func TestNoJSONDefaultingEnvelopeConstructor(t *testing.T) {
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatalf("read package dir: %v", err)
	}

	var offenders []string
	scanned := 0
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
			continue
		}
		src, err := os.ReadFile(filepath.Clean(name))
		if err != nil {
			t.Fatalf("read %s: %v", name, err)
		}
		scanned++
		for i, line := range strings.Split(string(src), "\n") {
			// NewEnvelopeAs is the correct one and shares the prefix, so match the
			// open paren to exclude it.
			if strings.Contains(line, "messages.NewEnvelope(") {
				offenders = append(offenders, fmt.Sprintf("%s:%d: %s", name, i+1, strings.TrimSpace(line)))
			}
		}
	}

	// A guard that scans nothing passes for the wrong reason. This is the same
	// class of defect as the thing it guards against.
	if scanned == 0 {
		t.Fatal("scanned no source files -- the guard is not looking at anything")
	}
	if len(offenders) > 0 {
		t.Errorf("messages.NewEnvelope hard-codes JSON; use messages.NewEnvelopeAs(r.enc, ...) instead.\n"+
			"On a sealed session these frames are rejected by the server:\n  %s",
			strings.Join(offenders, "\n  "))
	}
}
