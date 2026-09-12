package load

import (
	"bytes"
	"strings"
	"testing"
)

// The header must say which wire it measured. The two encodings differ ~5x in
// bytes per client from identical load, and a table without the word on it was
// once read as a Protobuf regression when it was the JSON arm all along.
func TestSummaryHeaderNamesTheEncoding(t *testing.T) {
	proto := healthyResult()
	proto.Config.Encoding = "proto"
	jsonRun := healthyResult()
	jsonRun.Config.Encoding = "json"

	cases := []struct {
		name    string
		results []*Result
		want    string
	}{
		{"single arm", []*Result{proto, proto}, "encoding=proto"},
		{"legacy arm", []*Result{jsonRun}, "encoding=json"},
		{"A/B sweep", []*Result{proto, jsonRun}, "encoding=mixed"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var buf bytes.Buffer
			WriteSummary(&buf, tc.results)
			head := strings.SplitN(buf.String(), "\n", 3)[1]
			if !strings.Contains(head, tc.want) {
				t.Errorf("header = %q, want it to carry %q", head, tc.want)
			}
		})
	}
}
