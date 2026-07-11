package enginesvc

import (
	"context"
	"testing"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestGetReplayStateEchoesCommitment(t *testing.T) {
	state, err := New().GetReplayState(context.Background(), &enginev1.GetReplayStateRequest{CommitmentId: "42"})
	if err != nil {
		t.Fatalf("GetReplayState: %v", err)
	}
	if state.GetCommitmentId() != "42" || state.GetRunning() {
		t.Fatalf("unexpected state: %+v", state)
	}
}
