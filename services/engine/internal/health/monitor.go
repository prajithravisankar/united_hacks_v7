// Package health watches brain's reachability and drives the engine's degraded/healthy status. The replay
// itself never depends on brain after startup — the curve is cached in-memory in the ticker — so an outage
// only changes the status broadcast to browsers, never the tick stream. The monitor is a single-goroutine
// state machine with hysteresis: it flips to "degraded" only after a run of consecutive failures and back to
// "healthy" only after a run of consecutive successes, so a momentary blip never spams a status change.
package health

import (
	"context"
	"log/slog"
	"time"

	"boys/engine/internal/clock"
)

// The two status values on the wire (see docs/ws-contract.md). "degraded" means the engine has lost brain
// and is serving the cached curve; "healthy" means brain is reachable again.
const (
	StatusHealthy  = "healthy"
	StatusDegraded = "degraded"
)

// Prober reports whether brain is reachable right now. A nil error is a healthy probe; any error is a failed
// one. brainclient implements this in production (a cheap in-memory RPC); tests use a fake they can toggle.
type Prober interface {
	Probe(ctx context.Context) error
}

// Broadcaster receives a confirmed status transition. The hub satisfies this via BroadcastStatus, which fans
// the status out to every connected client and updates the snapshot new clients receive.
type Broadcaster interface {
	BroadcastStatus(status string)
}

// Config tunes the monitor. The two thresholds give hysteresis: FailThreshold consecutive failed probes flip
// us to degraded, RecoverThreshold consecutive successful probes flip us back. Making them independent lets
// recovery be more cautious than failure (or vice versa) without any flapping.
type Config struct {
	Interval         time.Duration // wait between probes
	ProbeTimeout     time.Duration // per-probe deadline
	FailThreshold    int           // consecutive failures required to declare degraded
	RecoverThreshold int           // consecutive successes required to declare healthy again
	InitialStatus    string        // status at boot (StatusDegraded if brain was down at startup); "" = healthy
}

func (c Config) withDefaults() Config {
	if c.Interval <= 0 {
		c.Interval = 2 * time.Second
	}
	if c.ProbeTimeout <= 0 {
		c.ProbeTimeout = 2 * time.Second
	}
	if c.FailThreshold < 1 {
		c.FailThreshold = 1
	}
	if c.RecoverThreshold < 1 {
		c.RecoverThreshold = 1
	}
	return c
}

// Monitor polls brain and broadcasts confirmed status transitions. All mutable state is owned by the single
// Run goroutine — never locked, never shared — mirroring the hub's design.
type Monitor struct {
	prober      Prober
	broadcaster Broadcaster
	clk         clock.Clock
	cfg         Config
	logger      *slog.Logger

	// Run-goroutine-owned state (touched ONLY inside Run/probeOnce).
	status     string
	consecFail int
	consecOK   int
}

// New builds a monitor. It starts in cfg.InitialStatus (healthy unless brain was down at boot), matching the
// hub's initial status, so no spurious broadcast is emitted at startup — only real transitions are broadcast.
func New(prober Prober, broadcaster Broadcaster, clk clock.Clock, cfg Config, logger *slog.Logger) *Monitor {
	if logger == nil {
		logger = slog.Default()
	}
	initial := cfg.InitialStatus
	if initial != StatusDegraded {
		initial = StatusHealthy
	}
	return &Monitor{
		prober:      prober,
		broadcaster: broadcaster,
		clk:         clk,
		cfg:         cfg.withDefaults(),
		logger:      logger,
		status:      initial,
	}
}

// Run polls brain on the configured interval until the context is cancelled. It is the single owner of the
// monitor's state; exactly one Run per Monitor.
func (m *Monitor) Run(ctx context.Context) {
	for {
		t := m.clk.NewTimer(m.cfg.Interval)
		select {
		case <-ctx.Done():
			t.Stop()
			return
		case <-t.C():
		}
		m.probeOnce(ctx)
	}
}

// probeOnce runs one probe (bounded by ProbeTimeout) and feeds the outcome into the state machine.
func (m *Monitor) probeOnce(ctx context.Context) {
	probeCtx, cancel := context.WithTimeout(ctx, m.cfg.ProbeTimeout)
	err := m.prober.Probe(probeCtx)
	cancel()
	if err != nil {
		m.onFailure(err)
		return
	}
	m.onSuccess()
}

// onFailure records a failed probe. A single failure never resets nothing but the success streak; only a run
// of FailThreshold failures while healthy flips us to degraded, and it broadcasts exactly once.
func (m *Monitor) onFailure(err error) {
	m.consecFail++
	m.consecOK = 0
	if m.status == StatusHealthy && m.consecFail >= m.cfg.FailThreshold {
		m.status = StatusDegraded
		m.logger.Warn("brain unreachable — entering degraded mode; serving cached curve",
			"consecutiveFailures", m.consecFail, "err", err)
		m.broadcaster.BroadcastStatus(StatusDegraded)
	}
}

// onSuccess records a healthy probe. Only a run of RecoverThreshold successes while degraded flips us back to
// healthy, and it broadcasts exactly once. A failure mid-recovery resets the streak (see onFailure).
func (m *Monitor) onSuccess() {
	m.consecOK++
	m.consecFail = 0
	if m.status == StatusDegraded && m.consecOK >= m.cfg.RecoverThreshold {
		m.status = StatusHealthy
		m.logger.Info("brain reachable again — recovered to healthy", "consecutiveSuccesses", m.consecOK)
		m.broadcaster.BroadcastStatus(StatusHealthy)
	}
}
