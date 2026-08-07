package load

import (
	"math"
	"testing"
	"time"
)

func TestSamplesQuantile(t *testing.T) {
	tests := []struct {
		name string
		vals []float64
		q    float64
		want float64
	}{
		{"empty", nil, 0.5, 0},
		{"single", []float64{7}, 0.99, 7},
		{"p50 of 1..100", seq(1, 100), 0.50, 50},
		{"p95 of 1..100", seq(1, 100), 0.95, 95},
		{"p99 of 1..100", seq(1, 100), 0.99, 99},
		{"q<=0 is min", seq(1, 100), 0, 1},
		{"q>=1 is max", seq(1, 100), 1, 100},
		{"unsorted input", []float64{5, 1, 4, 2, 3}, 0.5, 3},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := NewSamples(0)
			for _, v := range tt.vals {
				s.Add(v)
			}
			if got := s.Quantile(tt.q); got != tt.want {
				t.Errorf("Quantile(%v) = %v, want %v", tt.q, got, tt.want)
			}
		})
	}
}

func TestSamplesDescribeAndMerge(t *testing.T) {
	a := NewSamples(0)
	a.AddDuration(10 * time.Millisecond)
	a.AddDuration(20 * time.Millisecond)
	b := NewSamples(0)
	b.AddDuration(30 * time.Millisecond)
	a.Merge(b)
	a.Merge(nil)

	d := a.Describe()
	if d.Count != 3 {
		t.Fatalf("Count = %d, want 3", d.Count)
	}
	if d.Mean != 20 {
		t.Errorf("Mean = %v, want 20", d.Mean)
	}
	if d.Max != 30 {
		t.Errorf("Max = %v, want 30", d.Max)
	}
	if d.P50 != 20 {
		t.Errorf("P50 = %v, want 20", d.P50)
	}
}

func TestSamplesEmptyDescribe(t *testing.T) {
	d := NewSamples(0).Describe()
	if d != (Dist{}) {
		t.Errorf("empty Describe() = %+v, want zero Dist", d)
	}
}

// Merging must not corrupt the sorted flag: a Quantile call before a Merge
// sorts the slice, and appending afterwards leaves it unsorted again.
func TestSamplesMergeInvalidatesSort(t *testing.T) {
	a := NewSamples(0)
	a.Add(5)
	a.Add(1)
	if got := a.Quantile(1); got != 5 {
		t.Fatalf("max = %v, want 5", got)
	}
	b := NewSamples(0)
	b.Add(0)
	a.Merge(b)
	if got := a.Quantile(0); got != 0 {
		t.Errorf("min after merge = %v, want 0", got)
	}
}

func seq(from, to int) []float64 {
	out := make([]float64, 0, to-from+1)
	for i := from; i <= to; i++ {
		out = append(out, float64(i))
	}
	return out
}

func TestHistogramQuantile(t *testing.T) {
	// Cumulative buckets: 90 ticks <=1ms, 99 <=10ms, 100 <=100ms.
	buckets := map[float64]float64{
		0.001: 90, 0.010: 99, 0.100: 100, math.Inf(1): 100,
	}
	tests := []struct {
		name string
		q    float64
		want float64
	}{
		// target = 50 -> inside the first bucket, interpolated from 0.
		{"p50", 0.50, 0.001 * 50 / 90},
		// target = 99 -> exactly the 0.010 edge.
		{"p99", 0.99, 0.010},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := HistogramQuantile(buckets, tt.q)
			if math.Abs(got-tt.want) > 1e-9 {
				t.Errorf("HistogramQuantile(%v) = %v, want %v", tt.q, got, tt.want)
			}
		})
	}
	if got := HistogramQuantile(nil, 0.99); !math.IsNaN(got) {
		t.Errorf("empty buckets = %v, want NaN", got)
	}
}

// A p99 that falls above the largest finite edge can only be reported as
// ">= that edge" — it must never be reported as +Inf.
func TestHistogramQuantileInfBucket(t *testing.T) {
	buckets := map[float64]float64{0.05: 90, math.Inf(1): 100}
	got := HistogramQuantile(buckets, 0.99)
	if got != 0.05 {
		t.Errorf("p99 = %v, want the 0.05 edge", got)
	}
}

func TestTickBudgetExceededRatio(t *testing.T) {
	budget := TickBudget.Seconds() // 0.0667
	tests := []struct {
		name      string
		buckets   map[float64]float64
		wantRatio float64
		wantEdge  float64
	}{
		{
			name:      "all ticks fast",
			buckets:   map[float64]float64{0.0005: 100, 0.05: 100, 0.075: 100, math.Inf(1): 100},
			wantRatio: 0,
			wantEdge:  0.05,
		},
		{
			// 10 of 100 ticks are above the 0.05 edge, which is the largest edge
			// still inside the budget.
			name:      "ten percent slow",
			buckets:   map[float64]float64{0.0005: 50, 0.05: 90, 0.075: 100, math.Inf(1): 100},
			wantRatio: 0.10,
			wantEdge:  0.05,
		},
		{
			name:      "empty",
			buckets:   nil,
			wantRatio: math.NaN(),
			wantEdge:  0,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ratio, edge := TickBudgetExceededRatio(tt.buckets, budget)
			if math.IsNaN(tt.wantRatio) {
				if !math.IsNaN(ratio) {
					t.Errorf("ratio = %v, want NaN", ratio)
				}
				return
			}
			if math.Abs(ratio-tt.wantRatio) > 1e-9 {
				t.Errorf("ratio = %v, want %v", ratio, tt.wantRatio)
			}
			if edge != tt.wantEdge {
				t.Errorf("edge = %v, want %v", edge, tt.wantEdge)
			}
		})
	}
}
