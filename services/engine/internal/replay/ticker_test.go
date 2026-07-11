package replay

import (
	"context"
	"fmt"
	"reflect"
	"sync"
	"testing"
	"time"

	"boys/engine/internal/clock"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

const stepAt1x = 30 * time.Millisecond // clean divisions: 30x -> 1ms, 8x -> 3.75ms, 1x -> 30ms

func makeTimeline(n int) []Point {
	pts := make([]Point, n)
	for i := range pts {
		pts[i] = Point{Date: fmt.Sprintf("2021-08-%02d", i+1), NavCents: int64(10000 + i*10)}
	}
	return pts
}

// newRunning starts a ticker on a fake clock and registers cleanup that cancels + drains it.
func newRunning(t *testing.T, timeline []Point) (*Ticker, *clock.FakeClock) {
	t.Helper()
	fc := clock.NewFake()
	tk := New(timeline, fc, stepAt1x)
	ctx, cancel := context.WithCancel(context.Background())
	go tk.Run(ctx)
	t.Cleanup(func() {
		cancel()
		for range tk.Ticks() { // drain until the driver closes it
		}
	})
	return tk, fc
}

// drive fires exactly n ticks at the given per-step delay and returns them in order.
func drive(t *testing.T, tk *Ticker, fc *clock.FakeClock, n int, delay time.Duration) []Tick {
	t.Helper()
	out := make([]Tick, 0, n)
	for i := 0; i < n; i++ {
		fc.BlockUntil(1)
		fc.Advance(delay)
		select {
		case tick := <-tk.Ticks():
			out = append(out, tick)
		case <-time.After(2 * time.Second):
			t.Fatalf("timed out waiting for tick %d", i)
		}
	}
	return out
}

func TestSameTimelineAndSpeedEmitIdenticalSequence(t *testing.T) {
	run := func() []Tick {
		tk, fc := newRunning(t, makeTimeline(10))
		tk.Start(30)
		return drive(t, tk, fc, 10, stepAt1x/30)
	}
	if a, b := run(), run(); !reflect.DeepEqual(a, b) {
		t.Fatalf("sequences differ:\n%v\n%v", a, b)
	}
}

func TestSpeedMathIsExact(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(3))
	tk.Start(30) // delay = 1ms
	fc.BlockUntil(1)

	fc.Advance(1*time.Millisecond - 1) // one nanosecond short
	select {
	case <-tk.Ticks():
		t.Fatal("tick fired before its exact deadline")
	default:
	}

	fc.Advance(1) // reach exactly the deadline
	select {
	case tick := <-tk.Ticks():
		if tick.Position != 0 {
			t.Fatalf("first tick position = %d, want 0", tick.Position)
		}
	case <-time.After(time.Second):
		t.Fatal("tick did not fire at its exact deadline")
	}
}

func TestPauseStopsEmissionsAndResumeContinuesExactly(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(10))
	tk.Start(30)
	got := drive(t, tk, fc, 2, stepAt1x/30) // positions 0, 1
	if got[1].Position != 1 {
		t.Fatalf("expected position 1, got %d", got[1].Position)
	}

	tk.Pause()
	if s := tk.State(); s.Running || s.Position != 2 {
		t.Fatalf("after pause: running=%v position=%d, want stopped at 2", s.Running, s.Position)
	}
	// Time passes while paused — nothing is emitted.
	fc.Advance(time.Second)
	select {
	case <-tk.Ticks():
		t.Fatal("a paused ticker emitted")
	default:
	}

	tk.Start(0) // resume, keep speed
	resumed := drive(t, tk, fc, 1, stepAt1x/30)
	if resumed[0].Position != 2 {
		t.Fatalf("resume emitted position %d, want 2 (no skip, no repeat)", resumed[0].Position)
	}
}

func TestPauseWhenPausedAndStartWhenRunningAreNoOps(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(10))
	tk.Start(30)
	drive(t, tk, fc, 1, stepAt1x/30)

	tk.Start(30) // resume while running — no-op
	if s := tk.State(); !s.Running || s.Position != 1 {
		t.Fatalf("start-while-running changed state: %+v", s)
	}
	tk.Pause()
	tk.Pause() // pause while paused — no-op
	if s := tk.State(); s.Running || s.Position != 1 {
		t.Fatalf("double pause changed state: %+v", s)
	}
}

func TestSeekJumpsToPositionAndPastEndCompletes(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(10))
	tk.Start(30)
	tk.Seek(5)
	got := drive(t, tk, fc, 1, stepAt1x/30)
	if got[0].Position != 5 {
		t.Fatalf("after seek(5), emitted position %d, want 5", got[0].Position)
	}

	tk.Seek(999)
	if s := tk.State(); !s.Done {
		t.Fatalf("seek past end did not complete: %+v", s)
	}
}

func TestSpeedChangeTakesEffectFromNextTick(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(10))
	tk.Start(1) // delay = 30ms
	drive(t, tk, fc, 1, stepAt1x/1)

	tk.SetSpeed(30) // delay now 1ms
	got := drive(t, tk, fc, 1, stepAt1x/30)
	if got[0].Position != 1 {
		t.Fatalf("after speed change, position = %d, want 1 (no reorder)", got[0].Position)
	}
	if s := tk.State(); s.Speed != 30 {
		t.Fatalf("speed = %v, want 30", s.Speed)
	}
}

func TestCompletionEmitsTerminalOnceAndRestarts(t *testing.T) {
	tk, fc := newRunning(t, makeTimeline(4))
	tk.Start(30)
	got := drive(t, tk, fc, 4, stepAt1x/30)

	terminals := 0
	for _, tick := range got {
		if tick.Terminal {
			terminals++
		}
	}
	if terminals != 1 || !got[3].Terminal {
		t.Fatalf("expected exactly one terminal at the end, got %d", terminals)
	}
	if s := tk.State(); !s.Done {
		t.Fatalf("not done after completion: %+v", s)
	}

	// Restart (demo-retake): replays from position 0 again.
	tk.Start(30)
	restart := drive(t, tk, fc, 1, stepAt1x/30)
	if restart[0].Position != 0 {
		t.Fatalf("restart emitted position %d, want 0", restart[0].Position)
	}
}

func TestConcurrentControlCallsAreRaceClean(t *testing.T) {
	// Real clock, tiny step, a drain goroutine, and many goroutines hammering the controls at once.
	// -race + goleak are the assertions.
	fc := clock.RealClock{}
	tk := New(makeTimeline(200), fc, 200*time.Microsecond)
	ctx, cancel := context.WithCancel(context.Background())
	go tk.Run(ctx)

	drained := make(chan struct{})
	go func() {
		defer close(drained)
		for range tk.Ticks() {
		}
	}()

	var wg sync.WaitGroup
	for i := 0; i < 40; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				switch (i + j) % 4 {
				case 0:
					tk.Start(float64(1 + (j % 30)))
				case 1:
					tk.Pause()
				case 2:
					tk.SetSpeed(float64(1 + (j % 8)))
				case 3:
					tk.Seek(j % 200)
				}
				_ = tk.State()
			}
		}(i)
	}
	wg.Wait()

	cancel()
	<-drained // driver closed Ticks(), drain goroutine exited — no leaks
}
