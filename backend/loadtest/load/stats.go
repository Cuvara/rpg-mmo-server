package load

import (
	"math"
	"sort"
	"time"
)

// Samples is an append-only float64 sample set (values in milliseconds) with
// percentile readout. Exact percentiles over the raw samples are used rather
// than a histogram: a 200-player 60s run produces ~180k samples per series,
// which is ~1.4MB — cheap enough that we do not trade accuracy for memory in a
// benchmark whose whole point is a defensible number.
type Samples struct {
	vals   []float64
	sorted bool
}

// NewSamples returns a sample set pre-sized for hint values.
func NewSamples(hint int) *Samples {
	if hint < 0 {
		hint = 0
	}
	return &Samples{vals: make([]float64, 0, hint)}
}

// Add records one observation.
func (s *Samples) Add(v float64) {
	s.vals = append(s.vals, v)
	s.sorted = false
}

// AddDuration records one observation in milliseconds.
func (s *Samples) AddDuration(d time.Duration) {
	s.Add(float64(d) / float64(time.Millisecond))
}

// Merge appends every observation from other.
func (s *Samples) Merge(other *Samples) {
	if other == nil || len(other.vals) == 0 {
		return
	}
	s.vals = append(s.vals, other.vals...)
	s.sorted = false
}

// Len is the number of observations.
func (s *Samples) Len() int { return len(s.vals) }

func (s *Samples) ensureSorted() {
	if !s.sorted {
		sort.Float64s(s.vals)
		s.sorted = true
	}
}

// Quantile returns the q-th quantile (0..1) using nearest-rank on the sorted
// samples. Returns 0 for an empty set.
func (s *Samples) Quantile(q float64) float64 {
	if len(s.vals) == 0 {
		return 0
	}
	s.ensureSorted()
	if q <= 0 {
		return s.vals[0]
	}
	if q >= 1 {
		return s.vals[len(s.vals)-1]
	}
	// Nearest-rank: ceil(q*N) clamped into [1,N], then 0-indexed.
	rank := int(math.Ceil(q*float64(len(s.vals)))) - 1
	if rank < 0 {
		rank = 0
	}
	if rank >= len(s.vals) {
		rank = len(s.vals) - 1
	}
	return s.vals[rank]
}

// Mean returns the arithmetic mean, or 0 for an empty set.
func (s *Samples) Mean() float64 {
	if len(s.vals) == 0 {
		return 0
	}
	var sum float64
	for _, v := range s.vals {
		sum += v
	}
	return sum / float64(len(s.vals))
}

// Max returns the largest observation, or 0 for an empty set.
func (s *Samples) Max() float64 {
	if len(s.vals) == 0 {
		return 0
	}
	s.ensureSorted()
	return s.vals[len(s.vals)-1]
}

// Dist is the serializable percentile readout of a Samples set. All values are
// milliseconds.
type Dist struct {
	Count int     `json:"count"`
	Mean  float64 `json:"mean_ms"`
	P50   float64 `json:"p50_ms"`
	P95   float64 `json:"p95_ms"`
	P99   float64 `json:"p99_ms"`
	Max   float64 `json:"max_ms"`
}

// Describe collapses the sample set into a Dist.
func (s *Samples) Describe() Dist {
	return Dist{
		Count: s.Len(),
		Mean:  round2(s.Mean()),
		P50:   round2(s.Quantile(0.50)),
		P95:   round2(s.Quantile(0.95)),
		P99:   round2(s.Quantile(0.99)),
		Max:   round2(s.Max()),
	}
}

func round2(v float64) float64 {
	return math.Round(v*100) / 100
}
