package main

import (
	"bytes"
	"log/slog"
	"strings"
	"testing"
)

// TestCheckRemovedTransportSettings pins the KCP-only migration's startup
// rules: GATEWAY_TRANSPORT=kcp is fatal (no client speaks KCP to the gateway),
// and the obsolete knobs are ignored loudly rather than silently.
func TestCheckRemovedTransportSettings(t *testing.T) {
	tests := []struct {
		name     string
		env      map[string]string
		wantErr  bool
		wantWarn string
	}{
		{name: "nothing set", env: nil},
		{name: "gateway tcp is accepted but flagged obsolete", env: map[string]string{"GATEWAY_TRANSPORT": "tcp"}, wantWarn: "GATEWAY_TRANSPORT is obsolete"},
		{name: "gateway kcp is fatal", env: map[string]string{"GATEWAY_TRANSPORT": "kcp"}, wantErr: true},
		{name: "gateway unknown is fatal", env: map[string]string{"GATEWAY_TRANSPORT": "quic"}, wantErr: true},
		{name: "allocator transport is ignored with a warning", env: map[string]string{"ALLOCATOR_TRANSPORT": "tcp"}, wantWarn: "ALLOCATOR_TRANSPORT is obsolete"},
		{name: "transport key on the gateway is flagged", env: map[string]string{"TRANSPORT_KEY": "abc"}, wantWarn: "does nothing"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var buf bytes.Buffer
			log := slog.New(slog.NewTextHandler(&buf, nil))
			err := checkRemovedTransportSettings(func(k string) string { return tt.env[k] }, log)
			if (err != nil) != tt.wantErr {
				t.Fatalf("err = %v, wantErr %v", err, tt.wantErr)
			}
			if tt.wantErr && !strings.Contains(err.Error(), "KCP") && !strings.Contains(err.Error(), "kcp") {
				t.Errorf("error does not name the gameplay transport: %v", err)
			}
			if tt.wantWarn != "" && !strings.Contains(buf.String(), tt.wantWarn) {
				t.Errorf("log %q does not contain %q", buf.String(), tt.wantWarn)
			}
			if tt.wantWarn == "" && !tt.wantErr && buf.Len() != 0 {
				t.Errorf("unexpected log output: %q", buf.String())
			}
		})
	}
}
