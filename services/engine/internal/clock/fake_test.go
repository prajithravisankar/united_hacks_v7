package clock

import (
	"testing"
	"time"

	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestTimerFiresOnlyAfterEnoughAdvance(t *testing.T) {
	c := NewFake()
	timer := c.NewTimer(100 * time.Millisecond)

	c.Advance(50 * time.Millisecond)
	select {
	case <-timer.C():
		t.Fatal("timer fired too early")
	default:
	}

	c.Advance(50 * time.Millisecond)
	select {
	case <-timer.C():
	default:
		t.Fatal("timer did not fire at its deadline")
	}
}

func TestBlockUntilWaitsForArmedTimer(t *testing.T) {
	c := NewFake()
	done := make(chan struct{})
	go func() {
		c.BlockUntil(1)
		close(done)
	}()

	select {
	case <-done:
		t.Fatal("BlockUntil returned before a timer was armed")
	case <-time.After(20 * time.Millisecond):
	}

	c.NewTimer(time.Second)
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("BlockUntil did not return after a timer was armed")
	}
}

func TestStopRemovesPendingTimer(t *testing.T) {
	c := NewFake()
	timer := c.NewTimer(time.Second)
	if !timer.Stop() {
		t.Fatal("Stop should return true for a pending timer")
	}
	c.Advance(2 * time.Second)
	select {
	case <-timer.C():
		t.Fatal("a stopped timer must not fire")
	default:
	}
}
