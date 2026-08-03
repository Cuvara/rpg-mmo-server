package errors

import "fmt"

// ErrorCode represents a game-specific error category.
type ErrorCode int

const (
	ErrUnauthorized ErrorCode = iota + 1000
	ErrInvalidInput
	ErrServerFull
	ErrNotFound
	ErrCooldown
	ErrOutOfRange
	ErrSpeedHack
	ErrDuplicate
	ErrInternal
	ErrNotImplemented
)

var codeNames = map[ErrorCode]string{
	ErrUnauthorized:   "UNAUTHORIZED",
	ErrInvalidInput:   "INVALID_INPUT",
	ErrServerFull:     "SERVER_FULL",
	ErrNotFound:       "NOT_FOUND",
	ErrCooldown:       "COOLDOWN",
	ErrOutOfRange:     "OUT_OF_RANGE",
	ErrSpeedHack:      "SPEED_HACK",
	ErrDuplicate:      "DUPLICATE",
	ErrInternal:       "INTERNAL",
	ErrNotImplemented: "NOT_IMPLEMENTED",
}

// GameError is a structured error with a code and message.
type GameError struct {
	Code    ErrorCode
	Message string
}

func (e *GameError) Error() string {
	name, ok := codeNames[e.Code]
	if !ok {
		name = "UNKNOWN"
	}
	return fmt.Sprintf("[%s] %s", name, e.Message)
}

// New creates a GameError with the given code and message.
func New(code ErrorCode, msg string) *GameError {
	return &GameError{Code: code, Message: msg}
}

// Newf creates a GameError with a formatted message.
func Newf(code ErrorCode, format string, args ...any) *GameError {
	return &GameError{Code: code, Message: fmt.Sprintf(format, args...)}
}

// Is checks whether an error is a GameError with a specific code.
func Is(err error, code ErrorCode) bool {
	ge, ok := err.(*GameError)
	return ok && ge.Code == code
}
