// Command engine is the BOYS real-time replay + WebSocket streaming service. It fetches the precomputed
// NAV curve from brain and (in later assignments) replays it deterministically to browsers over WebSocket.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/brainclient"
	"boys/engine/internal/clock"
	"boys/engine/internal/config"
	"boys/engine/internal/enginesvc"
	"boys/engine/internal/httpapi"
	"boys/engine/internal/replay"
	"google.golang.org/grpc"
)

// The demo commitment's stake and the fund window it's valued against (the historical slice the replay
// walks). Kept in sync with the ledger's settlement window.
const (
	demoPrincipalCents = 10000
	fundStartDate      = "2021-08-13"
	fundEndDate        = "2024-05-19"
	replayStepAt1x     = time.Second // one timeline point per second at 1x (≈33ms/point at 30x)
)

func main() {
	if err := run(); err != nil {
		slog.Error("engine exited with error", "err", err)
		os.Exit(1)
	}
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}

	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: cfg.LogLevel}))
	slog.SetDefault(logger)
	logger.Info("engine starting", "http", cfg.HTTPAddr, "grpc", cfg.GRPCAddr, "brain", cfg.BrainGRPCAddress)

	brain, err := brainclient.Dial(cfg.BrainGRPCAddress)
	if err != nil {
		return err
	}
	defer brain.Close()

	// Startup curve fetch — the demo timeline. A brain outage here is non-fatal (the engine runs degraded;
	// B20 makes the cache authoritative and adds recovery).
	var ready atomic.Bool
	fetchCtx, cancelFetch := context.WithTimeout(context.Background(), cfg.BrainTimeout)
	points, err := brain.FetchNavCurve(fetchCtx, cfg.DemoCommitmentID, demoPrincipalCents, fundStartDate, fundEndDate)
	cancelFetch()
	if err != nil {
		logger.Warn("could not fetch demo curve at startup; continuing degraded", "err", err)
	} else {
		ready.Store(true)
		logger.Info("fetched demo curve", "commitment", cfg.DemoCommitmentID, "points", len(points))
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	// Build the replay ticker from the fetched timeline (empty if brain was down — B20 makes it authoritative).
	timeline := make([]replay.Point, len(points))
	for i, p := range points {
		timeline[i] = replay.Point{Date: p.Date, NavCents: p.NavCents, Events: p.Events}
	}
	ticker := replay.New(timeline, clock.RealClock{}, replayStepAt1x)
	go ticker.Run(ctx)
	go func() { // consume emissions so the ticker never stalls (B19 replaces this drain with the WebSocket hub)
		for range ticker.Ticks() {
		}
	}()

	httpSrv := &http.Server{Addr: cfg.HTTPAddr, Handler: httpapi.Router(ready.Load)}
	grpcSrv := grpc.NewServer()
	enginev1.RegisterEngineServiceServer(grpcSrv, enginesvc.New(ticker, cfg.DemoCommitmentID))
	grpcLis, err := net.Listen("tcp", cfg.GRPCAddr)
	if err != nil {
		return err
	}

	errc := make(chan error, 2)
	go func() {
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errc <- err
		}
	}()
	go func() {
		if err := grpcSrv.Serve(grpcLis); err != nil {
			errc <- err
		}
	}()

	select {
	case <-ctx.Done():
		logger.Info("shutdown signal received, draining")
	case err := <-errc:
		logger.Error("server error; shutting down", "err", err)
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = httpSrv.Shutdown(shutdownCtx)
	grpcSrv.GracefulStop()
	logger.Info("engine stopped")
	return nil
}
