// Package enginesvc implements the EngineService gRPC API over the replay ticker.
package enginesvc

import (
	"context"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/replay"
)

// Server implements EngineService by delegating to the demo commitment's replay ticker. The control RPCs
// are thin, thread-safe adapters over the ticker's command API.
type Server struct {
	enginev1.UnimplementedEngineServiceServer
	ticker       *replay.Ticker
	commitmentID string
}

// New builds the service over a ticker for the given demo commitment.
func New(ticker *replay.Ticker, commitmentID string) *Server {
	return &Server{ticker: ticker, commitmentID: commitmentID}
}

// StartReplay begins or resumes the replay at the requested speed.
func (s *Server) StartReplay(_ context.Context, req *enginev1.StartReplayRequest) (*enginev1.ReplayState, error) {
	s.ticker.Start(req.GetSpeed())
	return s.state(), nil
}

// Pause halts the replay, preserving position.
func (s *Server) Pause(_ context.Context, _ *enginev1.PauseRequest) (*enginev1.ReplayState, error) {
	s.ticker.Pause()
	return s.state(), nil
}

// SetSpeed changes the multiplier from the next tick.
func (s *Server) SetSpeed(_ context.Context, req *enginev1.SetSpeedRequest) (*enginev1.ReplayState, error) {
	s.ticker.SetSpeed(req.GetSpeed())
	return s.state(), nil
}

// GetReplayState returns the current replay position/speed/state.
func (s *Server) GetReplayState(_ context.Context, _ *enginev1.GetReplayStateRequest) (*enginev1.ReplayState, error) {
	return s.state(), nil
}

func (s *Server) state() *enginev1.ReplayState {
	st := s.ticker.State()
	return &enginev1.ReplayState{
		CommitmentId:   s.commitmentID,
		Position:       int32(st.Position),
		Speed:          st.Speed,
		Running:        st.Running,
		CurrentSimDate: st.CurrentDate,
	}
}
