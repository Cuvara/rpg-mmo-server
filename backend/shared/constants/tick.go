package constants

import "time"

const (
	DefaultTickRate = 10 // Hz
	MaxTickRate     = 15 // Hz
	MinTickRate     = 5  // Hz
)

// TickDuration returns the duration of one tick at the given rate.
func TickDuration(hz int) time.Duration {
	if hz <= 0 {
		hz = DefaultTickRate
	}
	return time.Second / time.Duration(hz)
}
