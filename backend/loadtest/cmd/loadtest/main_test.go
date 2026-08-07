package main

import (
	"reflect"
	"testing"
	"time"
)

func TestParseSweep(t *testing.T) {
	tests := []struct {
		name    string
		in      string
		want    []int
		wantErr bool
	}{
		{"simple", "1,10,50", []int{1, 10, 50}, false},
		{"spaces and trailing comma", " 1 , 10 ,", []int{1, 10}, false},
		{"single", "200", []int{200}, false},
		{"not a number", "1,x", nil, true},
		{"zero rejected", "0,10", nil, true},
		{"negative rejected", "-5", nil, true},
		{"empty", "", nil, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := parseSweep(tt.in)
			if (err != nil) != tt.wantErr {
				t.Fatalf("parseSweep(%q) error = %v, wantErr %v", tt.in, err, tt.wantErr)
			}
			if !tt.wantErr && !reflect.DeepEqual(got, tt.want) {
				t.Errorf("parseSweep(%q) = %v, want %v", tt.in, got, tt.want)
			}
		})
	}
}

// The orchestration flags must be stripped before load.LoadConfig parses the
// rest, and every other flag must survive untouched in order.
func TestExtractSweepFlags(t *testing.T) {
	tests := []struct {
		name         string
		args         []string
		wantRest     []string
		wantSweep    []int
		wantCooldown time.Duration
		wantFail     bool
	}{
		{
			name:         "space separated",
			args:         []string{"-sweep", "1,10", "-players", "5"},
			wantRest:     []string{"-players", "5"},
			wantSweep:    []int{1, 10},
			wantCooldown: 20 * time.Second,
		},
		{
			name:         "equals form",
			args:         []string{"--sweep=1,10", "--cooldown=5s", "-duration", "30s"},
			wantRest:     []string{"-duration", "30s"},
			wantSweep:    []int{1, 10},
			wantCooldown: 5 * time.Second,
		},
		{
			name:         "bare boolean",
			args:         []string{"-fail-on-degraded", "-players", "5"},
			wantRest:     []string{"-players", "5"},
			wantCooldown: 20 * time.Second,
			wantFail:     true,
		},
		{
			name:         "no sweep flags at all",
			args:         []string{"-players", "5", "-json", "out.json"},
			wantRest:     []string{"-players", "5", "-json", "out.json"},
			wantCooldown: 20 * time.Second,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rest, sweep, cooldown, failOn, err := extractSweepFlags(tt.args)
			if err != nil {
				t.Fatalf("extractSweepFlags: %v", err)
			}
			if !reflect.DeepEqual(rest, tt.wantRest) {
				t.Errorf("rest = %v, want %v", rest, tt.wantRest)
			}
			if !reflect.DeepEqual(sweep, tt.wantSweep) {
				t.Errorf("sweep = %v, want %v", sweep, tt.wantSweep)
			}
			if cooldown != tt.wantCooldown {
				t.Errorf("cooldown = %v, want %v", cooldown, tt.wantCooldown)
			}
			if failOn != tt.wantFail {
				t.Errorf("failOnDegraded = %v, want %v", failOn, tt.wantFail)
			}
		})
	}
}

func TestExtractSweepFlagsErrors(t *testing.T) {
	tests := []struct {
		name string
		args []string
	}{
		{"sweep with no value", []string{"-sweep"}},
		{"sweep unparseable", []string{"-sweep", "abc"}},
		{"cooldown with no value", []string{"-cooldown"}},
		{"cooldown unparseable", []string{"-cooldown", "banana"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if _, _, _, _, err := extractSweepFlags(tt.args); err == nil {
				t.Errorf("extractSweepFlags(%v) = nil error, want an error", tt.args)
			}
		})
	}
}
