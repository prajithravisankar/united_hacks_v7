package clock

import (
	"sort"
	"sync"
	"time"
)

// FakeClock is a controllable clock for tests: time only moves when Advance is called, and BlockUntil lets a
// test wait for the driver to arm its next timer before advancing — so emissions are exact, not racy.
type FakeClock struct {
	mu       sync.Mutex
	now      time.Time
	timers   []*fakeTimer
	blockers []blocker
}

type blocker struct {
	count int
	ch    chan struct{}
}

// NewFake returns a fake clock started at an arbitrary fixed instant.
func NewFake() *FakeClock {
	return &FakeClock{now: time.Date(2021, 8, 13, 0, 0, 0, 0, time.UTC)}
}

func (c *FakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *FakeClock) NewTimer(d time.Duration) Timer {
	c.mu.Lock()
	defer c.mu.Unlock()
	t := &fakeTimer{clock: c, deadline: c.now.Add(d), c: make(chan time.Time, 1)}
	if !t.deadline.After(c.now) {
		t.c <- c.now // non-positive delay fires immediately
	} else {
		c.timers = append(c.timers, t)
	}
	c.wakeBlockers()
	return t
}

// Advance moves time forward and fires every timer whose deadline has now passed, in deadline order.
func (c *FakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
	sort.SliceStable(c.timers, func(i, j int) bool { return c.timers[i].deadline.Before(c.timers[j].deadline) })
	kept := c.timers[:0]
	for _, t := range c.timers {
		if !t.deadline.After(c.now) {
			t.c <- c.now // buffered (cap 1), never blocks under the lock
		} else {
			kept = append(kept, t)
		}
	}
	c.timers = kept
	c.wakeBlockers()
}

// BlockUntil blocks until at least n timers are pending. Used by tests to sync with the driver's next arm.
func (c *FakeClock) BlockUntil(n int) {
	c.mu.Lock()
	if len(c.timers) >= n {
		c.mu.Unlock()
		return
	}
	ch := make(chan struct{})
	c.blockers = append(c.blockers, blocker{count: n, ch: ch})
	c.mu.Unlock()
	<-ch
}

// wakeBlockers releases any blocker whose target is now met. Caller holds the lock.
func (c *FakeClock) wakeBlockers() {
	kept := c.blockers[:0]
	for _, b := range c.blockers {
		if len(c.timers) >= b.count {
			close(b.ch)
		} else {
			kept = append(kept, b)
		}
	}
	c.blockers = kept
}

type fakeTimer struct {
	clock    *FakeClock
	deadline time.Time
	c        chan time.Time
}

func (t *fakeTimer) C() <-chan time.Time { return t.c }

func (t *fakeTimer) Stop() bool {
	t.clock.mu.Lock()
	defer t.clock.mu.Unlock()
	for i, x := range t.clock.timers {
		if x == t {
			t.clock.timers = append(t.clock.timers[:i], t.clock.timers[i+1:]...)
			t.clock.wakeBlockers()
			return true
		}
	}
	return false // already fired or stopped
}
