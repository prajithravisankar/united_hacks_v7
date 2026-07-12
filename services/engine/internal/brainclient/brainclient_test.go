package brainclient

import (
	"context"
	"net"
	"testing"
	"time"

	brainv1 "boys/engine/gen/boys/brain/v1"
	commonv1 "boys/engine/gen/boys/common/v1"
	"go.uber.org/goleak"
	"google.golang.org/grpc"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

type fakeQuant struct {
	brainv1.UnimplementedQuantServiceServer
	curve *brainv1.NavCurve
}

func (f *fakeQuant) GetNavCurve(_ context.Context, _ *brainv1.GetNavCurveRequest) (*brainv1.NavCurve, error) {
	return f.curve, nil
}

// ListOpenMarkets backs the health Probe; the stub just needs to answer (brain-up) without an error.
func (f *fakeQuant) ListOpenMarkets(_ context.Context, _ *brainv1.ListOpenMarketsRequest) (*brainv1.OpenMarkets, error) {
	return &brainv1.OpenMarkets{}, nil
}

// startFakeBrain runs a stub QuantService on a loopback port and returns its address; the server is
// stopped (and its goroutines drained) on test cleanup so goleak stays clean.
func startFakeBrain(t *testing.T, curve *brainv1.NavCurve) string {
	t.Helper()
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	srv := grpc.NewServer()
	brainv1.RegisterQuantServiceServer(srv, &fakeQuant{curve: curve})
	go func() { _ = srv.Serve(lis) }()
	t.Cleanup(srv.Stop)
	return lis.Addr().String()
}

func TestFetchNavCurveReturnsPoints(t *testing.T) {
	curve := &brainv1.NavCurve{Points: []*brainv1.NavPoint{
		{Date: "2021-08-13", Nav: &commonv1.Money{Cents: 10000, Currency: "USD"}, Events: []string{"opening"}},
		{Date: "2021-08-14", Nav: &commonv1.Money{Cents: 10250, Currency: "USD"}},
	}}
	addr := startFakeBrain(t, curve)

	client, err := Dial(addr)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer client.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	points, err := client.FetchNavCurve(ctx, "1", 10000, "2021-08-13", "2021-08-14")
	if err != nil {
		t.Fatalf("FetchNavCurve: %v", err)
	}
	if len(points) != 2 {
		t.Fatalf("got %d points, want 2", len(points))
	}
	if points[0].NavCents != 10000 || points[1].NavCents != 10250 {
		t.Fatalf("unexpected navs: %+v", points)
	}
	if len(points[0].Events) != 1 || points[0].Events[0] != "opening" {
		t.Fatalf("unexpected events: %+v", points[0].Events)
	}
}

func TestFetchNavCurveUnavailableIsAnError(t *testing.T) {
	// Nothing listening on this port; the lazy client errors on the call, not on Dial.
	client, err := Dial("127.0.0.1:1")
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer client.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if _, err := client.FetchNavCurve(ctx, "1", 10000, "a", "b"); err == nil {
		t.Fatal("expected an error when brain is unavailable")
	}
}

func TestProbeSucceedsWhenBrainIsReachable(t *testing.T) {
	addr := startFakeBrain(t, &brainv1.NavCurve{})
	client, err := Dial(addr)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer client.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := client.Probe(ctx); err != nil {
		t.Fatalf("Probe against a reachable brain: %v", err)
	}
}

func TestProbeFailsWhenBrainIsUnavailable(t *testing.T) {
	client, err := Dial("127.0.0.1:1") // nothing listening
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer client.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := client.Probe(ctx); err == nil {
		t.Fatal("expected Probe to fail when brain is unreachable")
	}
}
