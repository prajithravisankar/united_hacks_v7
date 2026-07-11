// Package replay is the deterministic clock of the demo: it walks a precomputed timeline (NAV points +
// events) at a controllable speed, emitting an ordered tick stream. The driver is a single goroutine that
// owns all mutable state; every control call goes through a command channel, so it is race-free by
// construction and, on an injected clock, deterministic and testable in milliseconds.
package replay

import (
	"context"
	"time"

	"boys/engine/internal/clock"
)

// Point is one day of the timeline.
type Point struct {
	Date     string
	NavCents int64
	Events   []string
}

// Tick is one emitted step. Terminal marks the final tick of a completed replay (emitted exactly once).
type Tick struct {
	Position int
	Date     string
	NavCents int64
	Events   []string
	Terminal bool
}

// State is a snapshot of the ticker for GetReplayState.
type State struct {
	Position    int
	Speed       float64
	Running     bool
	Done        bool
	CurrentDate string
}

type cmdKind int

const (
	cmdStart cmdKind = iota
	cmdPause
	cmdSetSpeed
	cmdSeek
	cmdQuery
)

type command struct {
	kind  cmdKind
	speed float64
	pos   int
	reply chan State
	ack   chan struct{} // closed after the driver has applied the command (and stopped any live timer)
}

// Ticker replays a timeline. Construct with New, run the driver with Run, control it with the public
// methods, and consume emissions from Ticks.
type Ticker struct {
	timeline []Point
	clk      clock.Clock
	stepAt1x time.Duration // wall-clock per step at 1x; delay per step = stepAt1x / speed

	out  chan Tick
	cmds chan command
	done chan struct{}

	// Driver-owned — touched ONLY inside Run/apply/emit (never locked, never shared).
	position int
	speed    float64
	running  bool
	finished bool
}

// New builds a ticker. stepAt1x is the wall-clock time one step takes at 1x speed.
func New(timeline []Point, clk clock.Clock, stepAt1x time.Duration) *Ticker {
	return &Ticker{
		timeline: timeline,
		clk:      clk,
		stepAt1x: stepAt1x,
		out:      make(chan Tick),
		cmds:     make(chan command),
		done:     make(chan struct{}),
		speed:    1,
	}
}

// Ticks is the emission stream. Closed when the driver exits (context cancelled). A single consumer (the
// hub, or a test) reads it; the driver blocks on emit until it's read, keeping the sequence exact.
func (t *Ticker) Ticks() <-chan Tick { return t.out }

// Run drives the ticker until ctx is cancelled. Blocks; call in its own goroutine.
func (t *Ticker) Run(ctx context.Context) {
	defer close(t.done)
	defer close(t.out)

	for {
		if !t.running || t.finished || len(t.timeline) == 0 {
			// Idle: wait for a control command (no timer armed → no emissions).
			select {
			case <-ctx.Done():
				return
			case c := <-t.cmds:
				t.apply(c)
				ack(c)
			}
			continue
		}

		timer := t.clk.NewTimer(t.delay())
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case c := <-t.cmds:
			timer.Stop() // remove the live timer BEFORE acking, so a waiting test only sees the next arm
			t.apply(c)
			ack(c)
		case <-timer.C():
			if !t.emit(ctx) {
				return // context cancelled mid-emit
			}
		}
	}
}

func (t *Ticker) delay() time.Duration {
	return time.Duration(float64(t.stepAt1x) / t.speed)
}

func ack(c command) {
	if c.ack != nil {
		close(c.ack)
	}
}

// emit sends the current point, then advances (or finishes on the last point). Returns false if ctx is done.
func (t *Ticker) emit(ctx context.Context) bool {
	p := t.timeline[t.position]
	terminal := t.position == len(t.timeline)-1
	tick := Tick{Position: t.position, Date: p.Date, NavCents: p.NavCents, Events: p.Events, Terminal: terminal}

	select {
	case t.out <- tick:
	case <-ctx.Done():
		return false
	}

	if terminal {
		t.finished = true
		t.running = false
	} else {
		t.position++
	}
	return true
}

func (t *Ticker) apply(c command) {
	switch c.kind {
	case cmdStart:
		if c.speed > 0 {
			t.speed = c.speed
		}
		if t.finished { // restart after completion (the demo-retake path)
			t.finished = false
			t.position = 0
		}
		t.running = true
	case cmdPause:
		t.running = false
	case cmdSetSpeed:
		if c.speed > 0 {
			t.speed = c.speed // takes effect from the next tick; never reorders
		}
	case cmdSeek:
		switch {
		case c.pos < 0:
			t.position = 0
			t.finished = false
		case c.pos >= len(t.timeline):
			t.position = len(t.timeline) // past the end -> complete cleanly
			t.finished = true
			t.running = false
		default:
			t.position = c.pos
			t.finished = false
		}
	case cmdQuery:
		c.reply <- t.snapshot()
	}
}

func (t *Ticker) snapshot() State {
	date := ""
	switch {
	case t.position < len(t.timeline):
		date = t.timeline[t.position].Date
	case len(t.timeline) > 0:
		date = t.timeline[len(t.timeline)-1].Date
	}
	return State{Position: t.position, Speed: t.speed, Running: t.running, Done: t.finished, CurrentDate: date}
}

// ---- control API (safe for concurrent use; every call is serialized through the driver) ----

// Start begins or resumes replay. speed <= 0 keeps the current speed. After completion, Start restarts.
func (t *Ticker) Start(speed float64) { t.send(command{kind: cmdStart, speed: speed}) }

// Pause halts emissions, preserving the exact position.
func (t *Ticker) Pause() { t.send(command{kind: cmdPause}) }

// SetSpeed changes the multiplier from the next tick.
func (t *Ticker) SetSpeed(speed float64) { t.send(command{kind: cmdSetSpeed, speed: speed}) }

// Seek jumps to a position; past the end completes cleanly.
func (t *Ticker) Seek(pos int) { t.send(command{kind: cmdSeek, pos: pos}) }

// State returns a snapshot.
func (t *Ticker) State() State {
	reply := make(chan State, 1)
	t.send(command{kind: cmdQuery, reply: reply})
	select {
	case s := <-reply:
		return s
	case <-t.done:
		return State{}
	}
}

// send delivers a command and blocks until the driver has applied it (and stopped any live timer). This
// makes control calls synchronous, so a test's BlockUntil only ever observes the driver's next fresh arm.
func (t *Ticker) send(c command) {
	c.ack = make(chan struct{})
	select {
	case t.cmds <- c:
	case <-t.done: // driver exited; drop the command rather than block forever
		return
	}
	select {
	case <-c.ack:
	case <-t.done:
	}
}
