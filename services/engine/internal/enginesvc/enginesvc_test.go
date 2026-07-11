package enginesvc

import (
	"context"
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

func newService(t *testing.T) *Server {
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
	return New(ticker, "1")
}

func TestStartPauseAndStateRoundTrip(t *testing.T) {
	svc := newService(t)

	started, err := svc.StartReplay(context.Background(), &enginev1.StartReplayRequest{CommitmentId: "1", Speed: 8})
	if err != nil {
		t.Fatalf("StartReplay: %v", err)
	}
	if !started.GetRunning() || started.GetSpeed() != 8 || started.GetCommitmentId() != "1" {
		t.Fatalf("unexpected started state: %+v", started)
	}

	paused, err := svc.Pause(context.Background(), &enginev1.PauseRequest{CommitmentId: "1"})
	if err != nil {
		t.Fatalf("Pause: %v", err)
	}
	if paused.GetRunning() {
		t.Fatalf("expected not running after pause: %+v", paused)
	}

	state, err := svc.GetReplayState(context.Background(), &enginev1.GetReplayStateRequest{CommitmentId: "1"})
	if err != nil {
		t.Fatalf("GetReplayState: %v", err)
	}
	if state.GetRunning() {
		t.Fatalf("state still running: %+v", state)
	}
}
