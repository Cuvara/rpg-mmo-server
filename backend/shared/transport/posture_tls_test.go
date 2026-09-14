package transport

import "testing"

// TLS posture (ADR-23).
//
// The point of these cases is not that a boolean flows through a function. It
// is that the four security fields keep meaning four different things once a
// third encryption configuration exists — the KCP packet-crypt path, TLS, and
// the two of them combined, which is the case where a naive implementation
// starts lying about the key.

func TestPostureTLS(t *testing.T) {
	tests := []struct {
		name          string
		kind          string
		key           string
		addr          string
		tls           bool
		wantTLS       bool
		wantEncrypted bool
		wantAuth      bool
		wantCipher    string
		wantKeyIgnore bool
	}{
		{
			// The shipped default: every deploy path pins TLS off today.
			name: "tcp no tls is plaintext", kind: "tcp", addr: ":8000",
			wantCipher: CipherNone,
		},
		{
			name: "tcp with tls is encrypted AND authenticated", kind: "tcp", addr: ":8000", tls: true,
			wantTLS: true, wantEncrypted: true, wantAuth: true, wantCipher: CipherTLS,
		},
		{
			// The KCP path is CFB with a CRC32. Encrypted, never authenticated.
			// If this row ever reports authenticated, someone has folded two
			// different guarantees into one field.
			name: "kcp with key is encrypted but NOT authenticated", kind: "kcp", key: "abc", addr: ":8000",
			wantEncrypted: true, wantCipher: CipherAESCFB,
		},
		{
			// TLS needs a reliable ordered stream. Asking for it on KCP must not
			// silently produce an "encrypted" posture.
			name: "tls on kcp is refused, not honoured", kind: "kcp", addr: ":8000", tls: true,
			wantCipher: CipherNone, wantKeyIgnore: false,
		},
		{
			// ...and the KCP key still works on KCP even when TLS was asked for
			// and dropped, which is the only reason this row differs from the one
			// above.
			name: "tls on kcp with a key leaves the kcp crypt in force", kind: "kcp", key: "abc", addr: ":8000", tls: true,
			wantEncrypted: true, wantCipher: CipherAESCFB,
		},
		{
			// THE REGRESSION ROW. TCP + TLS + a transport key: the connection is
			// encrypted, and the key is still doing nothing. A KeyIgnored written
			// as `KeyConfigured && !Encrypted` answers false here and hides a
			// misconfiguration behind an unrelated feature being on.
			name: "tcp+tls+key still reports the key as ignored", kind: "tcp", key: "abc", addr: ":8000", tls: true,
			wantTLS: true, wantEncrypted: true, wantAuth: true, wantCipher: CipherTLS, wantKeyIgnore: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := PostureTLS(tt.kind, tt.key, tt.addr, tt.tls)
			if got.TLS != tt.wantTLS {
				t.Errorf("TLS = %v, want %v", got.TLS, tt.wantTLS)
			}
			if got.Encrypted != tt.wantEncrypted {
				t.Errorf("Encrypted = %v, want %v", got.Encrypted, tt.wantEncrypted)
			}
			if got.Authenticated != tt.wantAuth {
				t.Errorf("Authenticated = %v, want %v", got.Authenticated, tt.wantAuth)
			}
			if got.Cipher != tt.wantCipher {
				t.Errorf("Cipher = %q, want %q", got.Cipher, tt.wantCipher)
			}
			if got.KeyIgnored() != tt.wantKeyIgnore {
				t.Errorf("KeyIgnored() = %v, want %v (cipher=%q encrypted=%v)",
					got.KeyIgnored(), tt.wantKeyIgnore, got.Cipher, got.Encrypted)
			}
			if got.Summary == "" {
				t.Error("Summary is empty; an operator has nothing to act on")
			}
		})
	}
}

// TestPostureTLS_SummaryNamesWhatItDoesNotCover is the guard on the sentence
// that keeps this change honest.
//
// ADR-23's measurement is that the auth token is minted over a plaintext HTTP
// hop to Nakama. An operator who turns TLS on, reads "ENCRYPTED", and concludes
// their players' credentials are protected end to end is wrong — so the line
// that tells them they are encrypted has to tell them what is left. This test
// exists because that sentence is the kind of thing a later tidy-up deletes as
// verbose.
func TestPostureTLS_SummaryNamesWhatItDoesNotCover(t *testing.T) {
	got := PostureTLS("tcp", "", ":8000", true)
	for _, want := range []string{"Nakama", "ADR-23"} {
		if !contains(got.Summary, want) {
			t.Errorf("TLS summary does not mention %q; an operator would read it as end-to-end.\nsummary: %s",
				want, got.Summary)
		}
	}
}

// TestPosture_UnchangedForNonTLSCallers pins that adding TLS did not move any
// answer for the callers that do not use it — the game server's posture call
// among them.
func TestPosture_UnchangedForNonTLSCallers(t *testing.T) {
	for _, kind := range []string{"tcp", "kcp"} {
		for _, key := range []string{"", "abc"} {
			old := Posture(kind, key, ":8000")
			viaTLS := PostureTLS(kind, key, ":8000", false)
			if old != viaTLS {
				t.Errorf("Posture(%q,%q) != PostureTLS(...,false):\n old = %+v\n new = %+v", kind, key, old, viaTLS)
			}
			if old.TLS {
				t.Errorf("Posture(%q,%q) reported TLS true without TLS", kind, key)
			}
			if old.Authenticated {
				t.Errorf("Posture(%q,%q) reported Authenticated true; only TLS is authenticated", kind, key)
			}
		}
	}
}
