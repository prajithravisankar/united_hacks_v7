package enginesvc

import (
	"context"
	"sync"
	"testing"
	"time"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/clock"
	"boys/engine/internal/replay"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

// recordSink captures the running-state pushes the service makes after each control op.
type recordSink struct {
	mu  sync.Mutex
	got []bool
}

func (r *recordSink) SetRunning(running bool) {
	r.mu.Lock()
	r.got = append(r.got, running)
	r.mu.Unlock()
}

func (r *recordSink) last() (bool, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.got) == 0 {
		return false, false
	}
	return r.got[len(r.got)-1], true
}

func newService(t *testing.T) (*Server, *recordSink) {
	t.Helper()
	timeline := make([]replay.Point, 5)
	for i := range timeline {
		timeline[i] = replay.Point{Date: "2021-08-13", NavCents: int64(10000 + i)}
	}
	ticker := replay.New(timeline, clock.RealClock{}, 200*time.Microsecond)
	ctx, cancel := context.WithCancel(context.Background())
	go ticker.Run(ctx)
	go func() { // drain emissions so the ticker never stalls
		for range ticker.Ticks() {
		}
	}()
	t.Cleanup(cancel)
	sink := &recordSink{}
	return New(ticker, sink, "1"), sink
}

func TestStartPauseAndStateRoundTrip(t *testing.T) {
	svc, sink := newService(t)

	started, err := svc.StartReplay(context.Background(), &enginev1.StartReplayRequest{CommitmentId: "1", Speed: 8})
	if err != nil {
		t.Fatalf("StartReplay: %v", err)
	}
	if !started.GetRunning() || started.GetSpeed() != 8 || started.GetCommitmentId() != "1" {
		t.Fatalf("unexpected started state: %+v", started)
	}
	if r, ok := sink.last(); !ok || !r {
		t.Fatalf("StartReplay should push running=true to the sink, got %v (set=%v)", r, ok)
	}

	paused, err := svc.Pause(context.Background(), &enginev1.PauseRequest{CommitmentId: "1"})
	if err != nil {
		t.Fatalf("Pause: %v", err)
	}
	if paused.GetRunning() {
		t.Fatalf("expected not running after pause: %+v", paused)
	}
	// The whole point of the sink: a pause emits no tick, so the hub learns "not playing" only via this push.
	if r, ok := sink.last(); !ok || r {
		t.Fatalf("Pause should push running=false to the sink, got %v (set=%v)", r, ok)
	}

	state, err := svc.GetReplayState(context.Background(), &enginev1.GetReplayStateRequest{CommitmentId: "1"})
	if err != nil {
		t.Fatalf("GetReplayState: %v", err)
	}
	if state.GetRunning() {
		t.Fatalf("state still running: %+v", state)
	}
}

func TestSetSpeedChangesSpeedAndReportsRunning(t *testing.T) {
	svc, sink := newService(t)

	if _, err := svc.StartReplay(context.Background(), &enginev1.StartReplayRequest{CommitmentId: "1", Speed: 4}); err != nil {
		t.Fatalf("StartReplay: %v", err)
	}
	changed, err := svc.SetSpeed(context.Background(), &enginev1.SetSpeedRequest{CommitmentId: "1", Speed: 12})
	if err != nil {
		t.Fatalf("SetSpeed: %v", err)
	}
	if changed.GetSpeed() != 12 {
		t.Fatalf("speed = %v, want 12", changed.GetSpeed())
	}
	// SetSpeed leaves the replay playing, so it must push running=true to the sink (keeps the hub consistent).
	if !changed.GetRunning() {
		t.Fatalf("expected still running after SetSpeed: %+v", changed)
	}
	if r, ok := sink.last(); !ok || !r {
		t.Fatalf("SetSpeed should push running=true to the sink, got %v (set=%v)", r, ok)
	}
}
