package transport

import "testing"

// The case that matters most is the default one. Before this, the game server
// warned about KCP-without-a-key and about a key set on TCP, and said nothing at
// all about plain TCP with no key — the stock configuration, and the one with no
// encryption of any kind. The gateway was worse: it logged `encrypted` as
// `key != ""`, so a TCP gateway with a key set reported encrypted=true while
// sending cleartext.
func TestPosture(t *testing.T) {
	const key = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

	tests := []struct {
		name          string
		kind, key     string
		addr          string
		encrypted     bool
		keyConfigured bool
		keyIgnored    bool
		cipher        string
		wantInSummary string
	}{
		{
			name: "default tcp no key is plaintext", kind: "", key: "", addr: ":8000",
			encrypted: false, keyConfigured: false, keyIgnored: false,
			cipher: CipherNone, wantInSummary: "PLAINTEXT",
		},
		{
			// A key here does nothing. Reporting it as encryption is the defect
			// this replaces.
			name: "key on tcp is ignored not encryption", kind: KindTCP, key: key, addr: ":8000",
			encrypted: false, keyConfigured: true, keyIgnored: true,
			cipher: CipherNone, wantInSummary: "IGNORED",
		},
		{
			name: "kcp without key is plaintext", kind: KindKCP, key: "", addr: ":8000",
			encrypted: false, keyConfigured: false, keyIgnored: false,
			cipher: CipherNone, wantInSummary: KeyEnvVar,
		},
		{
			name: "kcp with key is the only encrypted case", kind: KindKCP, key: key, addr: ":8000",
			encrypted: true, keyConfigured: true, keyIgnored: false,
			cipher: CipherAESCFB, wantInSummary: "NOT AUTHENTICATED",
		},
		{
			// Whitespace is not a key: a variable set to spaces by a templating
			// mistake must not read as encryption configured.
			name: "blank key does not count", kind: KindKCP, key: "   ", addr: ":8000",
			encrypted: false, keyConfigured: false, keyIgnored: false,
			cipher: CipherNone, wantInSummary: "PLAINTEXT",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := Posture(tt.kind, tt.key, tt.addr)
			if p.Encrypted != tt.encrypted {
				t.Errorf("Encrypted = %v, want %v", p.Encrypted, tt.encrypted)
			}
			if p.KeyConfigured != tt.keyConfigured {
				t.Errorf("KeyConfigured = %v, want %v", p.KeyConfigured, tt.keyConfigured)
			}
			if p.KeyIgnored() != tt.keyIgnored {
				t.Errorf("KeyIgnored = %v, want %v", p.KeyIgnored(), tt.keyIgnored)
			}
			if p.Cipher != tt.cipher {
				t.Errorf("Cipher = %q, want %q", p.Cipher, tt.cipher)
			}
			if !contains(p.Summary, tt.wantInSummary) {
				t.Errorf("Summary = %q, want it to mention %q", p.Summary, tt.wantInSummary)
			}
			// Nothing this code can be configured for authenticates its packets.
			// If this ever fails, an AEAD landed and the claim must be re-earned.
			if p.Authenticated {
				t.Error("Authenticated = true, but no configuration authenticates packets yet")
			}
		})
	}
}

func TestPostureBindScopeIsPessimistic(t *testing.T) {
	tests := map[string]bool{
		// Loopback: unencrypted here is a local-dev choice, not an exposure.
		"127.0.0.1:8000": false,
		"localhost:8000": false,
		"[::1]:8000":     false,
		// Wildcards accept on every interface, and are the container shape.
		":8000":         true,
		"0.0.0.0:8000":  true,
		"[::]:8000":     true,
		"10.0.0.4:8000": true,
		// Unparseable is treated as exposed.
		"not-an-address": true,
		"":               true,
	}

	for addr, want := range tests {
		if got := Posture(KindTCP, "", addr).BindsBeyondLoopback; got != want {
			t.Errorf("addr %q: BindsBeyondLoopback = %v, want %v", addr, got, want)
		}
	}
}

// An encrypted listener is not scolded about its bind address: the off-host note
// belongs only to traffic anyone can read.
func TestPostureEncryptedListenerHasNoOffHostNote(t *testing.T) {
	p := Posture(KindKCP, "00112233445566778899aabbccddeeff", "0.0.0.0:8000")
	if !p.BindsBeyondLoopback {
		t.Fatal("expected a wildcard bind to count as beyond loopback")
	}
	if contains(p.Summary, "readable off-host") {
		t.Errorf("encrypted listener should not carry the off-host note: %q", p.Summary)
	}
}

func contains(s, sub string) bool {
	return len(sub) == 0 || (len(s) >= len(sub) && indexOf(s, sub) >= 0)
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
