package messages

import "math"

// Facing is carried on the wire as 16-bit binary radians BIASED BY ONE, so that
// the wire value zero can be reserved for "not sent".
//
// The reason is the trap this protocol has already been bitten by twice: proto3
// elides a zero, and 0.0 radians is a perfectly ordinary facing (due east). With
// a plain float, a sender meaning "east" and a sender predating the field are
// byte-identical, and no receiver rule can tell them apart. `speed` has that
// ambiguity and must document its way around it, because a zero speed is
// genuinely meaningful. Facing has no such excuse, so the ambiguity is designed
// out instead: every representable angle maps to a NON-ZERO wire value.
//
// It is also 1-3 bytes of varint instead of a float's fixed 5, on the hottest
// message in the protocol.
//
// Full rationale and the rejected alternatives: shared/docs/DESIGN.md, "Entity
// facing and action state on the wire".
const (
	// FacingBradSteps is the number of representable directions: a full turn is
	// divided into 65536 steps, so one step is 360/65536 = 0.0055 degrees.
	FacingBradSteps = 65536

	// FacingNotSent is the wire value meaning "this sender has no facing to
	// report". Reserved permanently; a real facing is always >= 1.
	FacingNotSent uint32 = 0
)

// FacingBradFromRadians encodes an angle (radians, counter-clockwise from +X)
// as a biased binary-radian wire value in [1, FacingBradSteps].
//
// Any angle is accepted: the value is wrapped into one turn first, so callers do
// not have to normalise. A non-finite angle encodes as FacingNotSent, because
// "NaN" is not a direction and silently shipping one would put an unrenderable
// value on the wire rather than an absent one.
func FacingBradFromRadians(radians float32) uint32 {
	if math.IsNaN(float64(radians)) || math.IsInf(float64(radians), 0) {
		return FacingNotSent
	}

	const twoPi = 2 * math.Pi

	// Wrap into [0, 2*Pi). math.Mod keeps the sign of the dividend, so a negative
	// angle needs one addition afterwards.
	wrapped := math.Mod(float64(radians), twoPi)
	if wrapped < 0 {
		wrapped += twoPi
	}

	step := int64(math.Round(wrapped / twoPi * FacingBradSteps))

	// Rounding can land exactly on FacingBradSteps (an angle just under a full
	// turn rounds up to the turn itself). That is the same direction as 0, so
	// fold it down rather than emitting an out-of-range value.
	if step >= FacingBradSteps {
		step = 0
	}

	// The +1 bias: step 0 (due east) becomes wire value 1, leaving 0 free to mean
	// "not sent".
	return uint32(step) + 1
}

// RadiansFromFacingBrad decodes a wire facing value.
//
// ok is false when the sender did not supply a facing (wire value 0). A receiver
// MUST honour that rather than treating the zero as "facing east": it should keep
// the entity's last known facing, or derive one from movement. Snapping to east
// instead means every entity from an old server points the same way, which reads
// as a content bug and gets debugged as one.
func RadiansFromFacingBrad(brad uint32) (radians float32, ok bool) {
	if brad == FacingNotSent {
		return 0, false
	}

	step := brad - 1
	if step >= FacingBradSteps {
		// Out of range: a peer that does not implement the bias, or a corrupt
		// value. Refuse it rather than aliasing it onto a wrong direction — a
		// wrong facing is harder to notice than an absent one.
		return 0, false
	}

	return float32(float64(step) / FacingBradSteps * 2 * math.Pi), true
}

// EntityAction mirrors the EntityAction enum in wire.proto: a coarse,
// level-triggered description of what an entity is doing, for a renderer to pick
// an animation from.
//
// ZERO IS RESERVED for "not sent" and idle is 1, deliberately. proto3 elides a
// zero enum, so making idle the zero value would make "standing still" and "this
// server does not know about actions" the same bytes — the same ambiguity the
// facing bias above exists to avoid, and the same one EntityType already avoids
// with ENTITY_TYPE_UNSPECIFIED.
type EntityAction uint32

const (
	// ActionUnspecified means "not sent" — never "idle". A receiver must keep
	// whatever it was showing rather than falling back to idle, or an old server
	// freezes every entity in the world into an idle pose.
	ActionUnspecified EntityAction = 0
	ActionIdle        EntityAction = 1
	ActionMoving      EntityAction = 2
	ActionAttacking   EntityAction = 3
	ActionDead        EntityAction = 4
)

// String renders an action for logs and test failures. Unknown values are shown
// numerically rather than mapped to a default: a value this build does not know
// is a newer peer's, and printing it as "idle" would hide that.
func (a EntityAction) String() string {
	switch a {
	case ActionUnspecified:
		return "unspecified"
	case ActionIdle:
		return "idle"
	case ActionMoving:
		return "moving"
	case ActionAttacking:
		return "attacking"
	case ActionDead:
		return "dead"
	default:
		return "action(" + itoa(uint32(a)) + ")"
	}
}

// itoa avoids pulling strconv into this file for one debug path.
func itoa(v uint32) string {
	if v == 0 {
		return "0"
	}
	var buf [10]byte
	i := len(buf)
	for v > 0 {
		i--
		buf[i] = byte('0' + v%10)
		v /= 10
	}
	return string(buf[i:])
}
