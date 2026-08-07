package load

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"math"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Scrape is one parsed /metrics snapshot: every sample keyed by its full
// `name{labels}` line, summed across label sets we do not care to distinguish.
type Scrape struct {
	// Gauges/counters, summed over all label sets of a metric family.
	Values map[string]float64
	// Histogram buckets keyed by family name -> upper bound -> cumulative count.
	Buckets map[string]map[float64]float64
	At      time.Time
}

// scrapeText parses the Prometheus text exposition format. It is intentionally
// minimal — enough for the counters, gauges and single histogram this benchmark
// reads, and no more. Label sets are collapsed by summing, which is correct here
// because every family we read is either unlabelled or labelled only by map_id
// and we always target a single map.
func scrapeText(body string, at time.Time) *Scrape {
	s := &Scrape{
		Values:  map[string]float64{},
		Buckets: map[string]map[float64]float64{},
		At:      at,
	}
	sc := bufio.NewScanner(strings.NewReader(body))
	sc.Buffer(make([]byte, 0, 64*1024), 1<<20)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		// `name{labels} value [timestamp]`
		sp := strings.LastIndex(line, " ")
		if sp < 0 {
			continue
		}
		key, valStr := line[:sp], line[sp+1:]
		val, err := strconv.ParseFloat(valStr, 64)
		if err != nil {
			continue
		}
		name, labels := splitMetric(key)
		if strings.HasSuffix(name, "_bucket") {
			family := strings.TrimSuffix(name, "_bucket")
			le, ok := labelValue(labels, "le")
			if !ok {
				continue
			}
			bound, err := parseBound(le)
			if err != nil {
				continue
			}
			if s.Buckets[family] == nil {
				s.Buckets[family] = map[float64]float64{}
			}
			s.Buckets[family][bound] += val
			continue
		}
		s.Values[name] += val
		// Keep the outcome dimension too: gateway_auth_total is split by
		// result="ok"/"fail" and summing them would report every attempt as a
		// success. Stored as `family|result=ok`.
		for _, dim := range []string{"result", "reason", "status"} {
			if v, ok := labelValue(labels, dim); ok {
				s.Values[name+"|"+dim+"="+v] += val
			}
		}
	}
	return s
}

func splitMetric(key string) (name, labels string) {
	i := strings.IndexByte(key, '{')
	if i < 0 {
		return key, ""
	}
	j := strings.LastIndexByte(key, '}')
	if j < i {
		return key[:i], ""
	}
	return key[:i], key[i+1 : j]
}

// labelValue pulls one label out of a raw `a="1",b="2"` label string.
func labelValue(labels, want string) (string, bool) {
	for _, part := range strings.Split(labels, ",") {
		eq := strings.IndexByte(part, '=')
		if eq < 0 {
			continue
		}
		if strings.TrimSpace(part[:eq]) != want {
			continue
		}
		return strings.Trim(strings.TrimSpace(part[eq+1:]), `"`), true
	}
	return "", false
}

func parseBound(le string) (float64, error) {
	if le == "+Inf" {
		return math.Inf(1), nil
	}
	return strconv.ParseFloat(le, 64)
}

// Get returns a metric value, or 0 when absent.
func (s *Scrape) Get(name string) float64 {
	if s == nil {
		return 0
	}
	return s.Values[name]
}

// fetchScrape GETs a /metrics endpoint and parses it. An empty url yields nil,
// which every reader tolerates.
func fetchScrape(ctx context.Context, hc *http.Client, url string) (*Scrape, error) {
	if url == "" {
		return nil, nil
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	resp, err := hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("GET %s: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GET %s: status %d", url, resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", url, err)
	}
	return scrapeText(string(body), time.Now()), nil
}

// BucketDelta subtracts a `before` histogram from an `after` one, yielding the
// bucket counts accumulated during the window. Scraping a process-lifetime
// histogram once would average in every tick since boot — including the idle
// ticks before the load started — and hide the peak entirely.
func BucketDelta(before, after *Scrape, family string) map[float64]float64 {
	if after == nil {
		return nil
	}
	// Every bucket present in `after` is kept, including ones whose delta is zero.
	// A Prometheus histogram is CUMULATIVE, so dropping an empty bucket does not
	// remove an empty bin — it deletes an edge, and every reader that walks the
	// edges then draws the wrong conclusion. Concretely: under heavy load no tick
	// lands in the sub-millisecond buckets, so those deltas are zero; dropping
	// them leaves no edge at or below the 66.67ms budget at all, and
	// TickBudgetExceededRatio reports "no ticks over budget" for a server whose
	// p99 is 486ms.
	out := map[float64]float64{}
	for bound, v := range after.Buckets[family] {
		prev := 0.0
		if before != nil {
			prev = before.Buckets[family][bound]
		}
		d := v - prev
		if d < 0 {
			d = 0 // counter reset mid-window
		}
		out[bound] = d
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

// HistogramQuantile estimates a quantile from cumulative bucket counts using
// linear interpolation inside the containing bucket — the same estimator
// Prometheus' histogram_quantile() uses.
//
// The estimate is only as good as the bucket edges. The game server's tick
// histogram has an edge at 0.075s, straddling the 66.67ms budget, so a p99 that
// lands in the 0.05..0.075 bucket is reported as somewhere in that range and
// cannot by itself prove the budget was met. TickBudgetExceededRatio answers
// that question exactly instead.
func HistogramQuantile(buckets map[float64]float64, q float64) float64 {
	if len(buckets) == 0 {
		return math.NaN()
	}
	bounds := make([]float64, 0, len(buckets))
	for b := range buckets {
		bounds = append(bounds, b)
	}
	sort.Float64s(bounds)

	total := buckets[bounds[len(bounds)-1]]
	if total <= 0 {
		return math.NaN()
	}
	target := q * total
	prevBound, prevCount := 0.0, 0.0
	for _, b := range bounds {
		count := buckets[b]
		if count < target {
			prevBound, prevCount = b, count
			continue
		}
		if math.IsInf(b, 1) {
			// Everything above the last finite edge: we can only say ">= edge".
			return prevBound
		}
		if count == prevCount {
			return b
		}
		frac := (target - prevCount) / (count - prevCount)
		return prevBound + frac*(b-prevBound)
	}
	return bounds[len(bounds)-1]
}

// TickBudgetExceededRatio is the fraction of ticks in the window that took
// longer than the budget. Unlike an interpolated p99 it needs no assumption
// about the distribution inside a bucket: the game server's histogram has an
// exact edge at 0.075s and one at 0.05s, so the count above 0.05s is a hard
// upper bound on "ticks that were anywhere near overrunning".
//
// Returns the ratio and the bucket edge it was measured at.
func TickBudgetExceededRatio(buckets map[float64]float64, budget float64) (ratio, edge float64) {
	if len(buckets) == 0 {
		return math.NaN(), 0
	}
	bounds := make([]float64, 0, len(buckets))
	for b := range buckets {
		bounds = append(bounds, b)
	}
	sort.Float64s(bounds)
	total := buckets[bounds[len(bounds)-1]]
	if total <= 0 {
		return math.NaN(), 0
	}
	// Largest finite edge that is <= budget: everything above it is at risk.
	edge = 0
	below := 0.0
	for _, b := range bounds {
		if math.IsInf(b, 1) || b > budget {
			break
		}
		edge, below = b, buckets[b]
	}
	if edge == 0 {
		return math.NaN(), 0
	}
	return (total - below) / total, edge
}
