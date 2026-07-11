// Package clock abstracts time so the replay ticker is driven by an injected clock — real in production,
// a controllable fake in tests. That's what makes the ticker deterministic and testable in milliseconds.
package clock

import "time"

// Clock is the minimal time surface the ticker needs.
type Clock interface {
	Now() time.Time
	NewTimer(d time.Duration) Timer
}

// Timer fires once after its delay. Stop cancels it (returns false if it already fired or was stopped).
type Timer interface {
	C() <-chan time.Time
	Stop() bool
}

// RealClock is the production clock backed by the time package.
type RealClock struct{}

func (RealClock) Now() time.Time { return time.Now() }

func (RealClock) NewTimer(d time.Duration) Timer { return &realTimer{t: time.NewTimer(d)} }

type realTimer struct{ t *time.Timer }

func (r *realTimer) C() <-chan time.Time { return r.t.C }

func (r *realTimer) Stop() bool { return r.t.Stop() }
