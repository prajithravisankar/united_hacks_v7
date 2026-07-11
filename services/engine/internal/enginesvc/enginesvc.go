// Package enginesvc implements the EngineService gRPC API. B17 ships a stub; B18 backs it with the
// deterministic replay ticker.
package enginesvc

import (
	"context"

	enginev1 "boys/engine/gen/boys/engine/v1"
)

// Server implements EngineService. The B17 stub reports a not-running state; the control RPCs are
// unimplemented until B18 wires the ticker in.
type Server struct {
	enginev1.UnimplementedEngineServiceServer
}

// New builds the stub server.
func New() *Server { return &Server{} }

// GetReplayState returns a placeholder not-running state for the requested commitment.
func (s *Server) GetReplayState(_ context.Context, req *enginev1.GetReplayStateRequest) (*enginev1.ReplayState, error) {
	return &enginev1.ReplayState{CommitmentId: req.GetCommitmentId(), Running: false}, nil
}
