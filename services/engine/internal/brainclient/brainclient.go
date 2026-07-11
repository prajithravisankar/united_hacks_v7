// Package brainclient fetches the precomputed NAV curve from brain over gRPC. It is the engine's only
// dependency on brain, and the seam where B20's degradation cache lives.
package brainclient

import (
	"context"
	"fmt"

	brainv1 "boys/engine/gen/boys/brain/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

// NavPoint is one day of the replay timeline: the action-pool value plus the human-readable events that
// moved it. NAV is integer cents (never float), consistent with the rest of the system.
type NavPoint struct {
	Date     string
	NavCents int64
	Events   []string
}

// Client wraps brain's QuantService. Safe for concurrent use.
type Client struct {
	conn  *grpc.ClientConn
	quant brainv1.QuantServiceClient
}

// Dial creates a lazy client (no connection until the first RPC), so construction never blocks.
func Dial(address string) (*Client, error) {
	conn, err := grpc.NewClient(address, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return nil, fmt.Errorf("dial brain %q: %w", address, err)
	}
	return &Client{conn: conn, quant: brainv1.NewQuantServiceClient(conn)}, nil
}

// Close releases the underlying connection and its goroutines.
func (c *Client) Close() error { return c.conn.Close() }

// FetchNavCurve returns the commitment's NAV curve. The returned error is non-nil (and the slice nil) when
// brain is unavailable — callers degrade rather than crash.
func (c *Client) FetchNavCurve(ctx context.Context, commitmentID string, principalCents int64, start, end string) ([]NavPoint, error) {
	resp, err := c.quant.GetNavCurve(ctx, &brainv1.GetNavCurveRequest{
		CommitmentId:   commitmentID,
		PrincipalCents: principalCents,
		StartDate:      start,
		EndDate:        end,
		DriveMode:      brainv1.DriveMode_DRIVE_MODE_AUTO,
	})
	if err != nil {
		return nil, fmt.Errorf("fetch nav curve for %s: %w", commitmentID, err)
	}

	points := make([]NavPoint, 0, len(resp.GetPoints()))
	for _, p := range resp.GetPoints() {
		points = append(points, NavPoint{
			Date:     p.GetDate(),
			NavCents: p.GetNav().GetCents(),
			Events:   p.GetEvents(),
		})
	}
	return points, nil
}
