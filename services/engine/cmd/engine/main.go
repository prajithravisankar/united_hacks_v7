// Command engine is the BOYS real-time replay + WebSocket streaming service. It fetches the precomputed
// NAV curve from brain and (in later assignments) replays it deterministically to browsers over WebSocket.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net"
	"net/http"
	"net/http/pprof"
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
	"boys/engine/internal/health"
	"boys/engine/internal/httpapi"
	"boys/engine/internal/hub"
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
	wsSendBuffer       = 64          // per-client queue depth before a slow browser is dropped

	// Degradation monitor: probe brain every 2s; flip to degraded after 2 straight misses, back to healthy
	// after 2 straight hits. The hysteresis keeps a one-off blip from flapping the on-screen status.
	healthProbeInterval = 2 * time.Second
	healthProbeTimeout  = 2 * time.Second
	healthFailThreshold = 2
	healthRecoverThresh = 2
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

	// Startup curve fetch — the demo timeline. A brain outage here is non-fatal: the cached curve is
	// authoritative, so the engine runs degraded until the monitor sees brain recover.
	var ready atomic.Bool
	bootStatus := health.StatusHealthy
	fetchCtx, cancelFetch := context.WithTimeout(context.Background(), cfg.BrainTimeout)
	points, err := brain.FetchNavCurve(fetchCtx, cfg.DemoCommitmentID, demoPrincipalCents, fundStartDate, fundEndDate)
	cancelFetch()
	if err != nil {
		bootStatus = health.StatusDegraded // brain down at boot: report degraded from the first frame, not healthy
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

	// Self-heal: if the boot fetch failed (brain was down at startup → empty timeline), keep retrying in the
	// background and load the curve into the ticker once brain returns. Without this the engine would serve a
	// permanently-empty stream (and later mislead clients with a "healthy" status) until a manual restart.
	if !ready.Load() {
		go retryCurveFetch(ctx, brain, ticker, &ready, cfg.DemoCommitmentID, cfg.BrainTimeout, logger)
	}

	// The WebSocket hub is the single consumer of the tick stream; it fans out to every connected browser.
	liveHub := hub.NewHub(ticker, cfg.DemoCommitmentID, wsSendBuffer, bootStatus)
	go liveHub.Run(ctx)

	// The degradation monitor probes brain and broadcasts a status change through the hub when brain drops
	// (degraded) or comes back (healthy). The replay keeps running off the cached curve either way.
	monitor := health.New(brain, liveHub, clock.RealClock{}, health.Config{
		Interval:         healthProbeInterval,
		ProbeTimeout:     healthProbeTimeout,
		FailThreshold:    healthFailThreshold,
		RecoverThreshold: healthRecoverThresh,
		InitialStatus:    bootStatus,
	}, logger)
	go monitor.Run(ctx)

	mux := httpapi.Router(ready.Load)
	mux.HandleFunc("/ws/live", liveHub.Handler())
	httpapi.RegisterReplayControl(mux, ticker, liveHub) // browser-drivable play/pause/speed for the board
	if cfg.EnablePprof {
		// Opt-in profiling (off by default; loopback-only in compose). Used by the R5 soak to sample the
		// engine's goroutine count and heap. pprof.Index serves /debug/pprof/{goroutine,heap,...}.
		mux.HandleFunc("/debug/pprof/", pprof.Index)
		mux.HandleFunc("/debug/pprof/profile", pprof.Profile)
		logger.Info("pprof enabled on the HTTP listener at /debug/pprof/")
	}
	httpSrv := &http.Server{Addr: cfg.HTTPAddr, Handler: mux}
	grpcSrv := grpc.NewServer()
	enginev1.RegisterEngineServiceServer(grpcSrv, enginesvc.New(ticker, liveHub, cfg.DemoCommitmentID))
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

// retryCurveFetch keeps trying to fetch the demo curve until brain returns it, then loads it into the ticker
// and marks the engine ready — the self-heal for an engine that booted while brain was down. Exits on the
// first success or on ctx cancel.
func retryCurveFetch(ctx context.Context, brain *brainclient.Client, ticker *replay.Ticker, ready *atomic.Bool,
	commitmentID string, timeout time.Duration, logger *slog.Logger) {
	t := time.NewTicker(2 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			fetchCtx, cancel := context.WithTimeout(ctx, timeout)
			points, err := brain.FetchNavCurve(fetchCtx, commitmentID, demoPrincipalCents, fundStartDate, fundEndDate)
			cancel()
			if err != nil || len(points) == 0 {
				continue
			}
			timeline := make([]replay.Point, len(points))
			for i, p := range points {
				timeline[i] = replay.Point{Date: p.Date, NavCents: p.NavCents, Events: p.Events}
			}
			ticker.Load(timeline)
			ready.Store(true)
			logger.Info("fetched demo curve after startup retry; engine now serving", "points", len(timeline))
			return
		}
	}
}
