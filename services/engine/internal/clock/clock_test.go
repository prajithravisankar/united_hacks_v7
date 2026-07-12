package clock

import (
	"testing"
	"time"
)

func TestRealClockTimerFiresAndNowAdvances(t *testing.T) {
	c := RealClock{}
	before := c.Now()
	timer := c.NewTimer(5 * time.Millisecond)
	select {
	case <-timer.C():
	case <-time.After(2 * time.Second):
		t.Fatal("real timer never fired")
	}
	if !c.Now().After(before) {
		t.Fatal("Now() did not advance after waiting")
	}
}

func TestRealClockStopOnLiveTimer(t *testing.T) {
	c := RealClock{}
	timer := c.NewTimer(time.Hour) // far future, so Stop catches it live
	if !timer.Stop() {
		t.Fatal("Stop on a live timer should return true")
	}
}

func TestFakeClockNowAdvances(t *testing.T) {
	c := NewFake()
	start := c.Now()
	c.Advance(2 * time.Second)
	if got := c.Now().Sub(start); got != 2*time.Second {
		t.Fatalf("Now advanced by %v, want 2s", got)
	}
}
