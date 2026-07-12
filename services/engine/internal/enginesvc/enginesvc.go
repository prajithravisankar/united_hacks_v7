// Package enginesvc implements the EngineService gRPC API over the replay ticker.
package enginesvc

import (
	"context"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/replay"
)

// RunningSink is told whether the replay is playing after each control op, so the WebSocket hub can report
// the paused state correctly (a pause emits no tick, so the hub can't infer it from the stream). The hub
// implements this via SetRunning.
type RunningSink interface {
	SetRunning(running bool)
}

// Server implements EngineService by delegating to the demo commitment's replay ticker. The control RPCs
// are thin, thread-safe adapters over the ticker's command API.
type Server struct {
	enginev1.UnimplementedEngineServiceServer
	ticker       *replay.Ticker
	sink         RunningSink // notified of running-state changes; may be nil (tests)
	commitmentID string
}

// New builds the service over a ticker for the given demo commitment. sink (the hub) is notified of
// running-state changes; pass nil when running without a hub (tests).
func New(ticker *replay.Ticker, sink RunningSink, commitmentID string) *Server {
	return &Server{ticker: ticker, sink: sink, commitmentID: commitmentID}
}

// StartReplay begins or resumes the replay at the requested speed.
func (s *Server) StartReplay(_ context.Context, req *enginev1.StartReplayRequest) (*enginev1.ReplayState, error) {
	s.ticker.Start(req.GetSpeed())
	st := s.state()
	s.notifyRunning(st.GetRunning())
	return st, nil
}

// Pause halts the replay, preserving position.
func (s *Server) Pause(_ context.Context, _ *enginev1.PauseRequest) (*enginev1.ReplayState, error) {
	s.ticker.Pause()
	st := s.state()
	s.notifyRunning(st.GetRunning())
	return st, nil
}

// SetSpeed changes the multiplier from the next tick.
func (s *Server) SetSpeed(_ context.Context, req *enginev1.SetSpeedRequest) (*enginev1.ReplayState, error) {
	s.ticker.SetSpeed(req.GetSpeed())
	st := s.state()
	s.notifyRunning(st.GetRunning())
	return st, nil
}

func (s *Server) notifyRunning(running bool) {
	if s.sink != nil {
		s.sink.SetRunning(running)
	}
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
