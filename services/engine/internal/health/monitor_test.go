package health

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"sync"
	"testing"
	"time"

	"boys/engine/internal/clock"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

const interval = time.Second

// fakeProber is a togglable brain: healthy until told to fail, and back again.
type fakeProber struct {
	mu   sync.Mutex
	fail bool
}

func (p *fakeProber) Probe(context.Context) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.fail {
		return errors.New("brain down")
	}
	return nil
}

func (p *fakeProber) setFail(v bool) {
	p.mu.Lock()
	p.fail = v
	p.mu.Unlock()
}

// recorder captures every broadcast so a test can assert the exact transition sequence.
type recorder struct {
	mu  sync.Mutex
	got []string
}

func (r *recorder) BroadcastStatus(s string) {
	r.mu.Lock()
	r.got = append(r.got, s)
	r.mu.Unlock()
}

func (r *recorder) sequence() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]string(nil), r.got...)
}

// harness runs a monitor on a fake clock so every probe fires exactly when the test says.
type harness struct {
	fc   *clock.FakeClock
	rec  *recorder
	stop func()
}

func newHarness(t *testing.T, prober Prober, cfg Config) *harness {
	t.Helper()
	fc := clock.NewFake()
	rec := &recorder{}
	m := New(prober, rec, fc, cfg, slog.New(slog.NewTextHandler(io.Discard, nil)))
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		m.Run(ctx)
		close(done)
	}()
	h := &harness{fc: fc, rec: rec, stop: func() {
		cancel()
		<-done
	}}
	t.Cleanup(h.stop)
	return h
}

// probe drives exactly one probe: wait for the monitor to arm its timer, fire it, then wait for it to re-arm
// (which happens only after the probe + any broadcast have completed). Fully deterministic, no sleeps.
func (h *harness) probe() {
	h.fc.BlockUntil(1)
	h.fc.Advance(interval)
	h.fc.BlockUntil(1) // re-armed => the probe (and its broadcast, if any) is done
}

func (h *harness) probeN(n int) {
	for i := 0; i < n; i++ {
		h.probe()
	}
}

func assertSeq(t *testing.T, got, want []string) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("broadcasts = %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("broadcasts = %v, want %v", got, want)
		}
	}
}

func TestStaysSilentWhileBrainIsHealthy(t *testing.T) {
	h := newHarness(t, &fakeProber{}, Config{Interval: interval, FailThreshold: 2, RecoverThreshold: 2})
	h.probeN(5)
	if seq := h.rec.sequence(); len(seq) != 0 {
		t.Fatalf("expected no broadcasts while healthy, got %v", seq)
	}
}

func TestDegradesOnlyAfterThresholdConsecutiveFailures(t *testing.T) {
	p := &fakeProber{fail: true}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 3, RecoverThreshold: 1})

	h.probeN(2) // two failures — still below the threshold
	if seq := h.rec.sequence(); len(seq) != 0 {
		t.Fatalf("degraded too early after 2 failures: %v", seq)
	}
	h.probe() // third consecutive failure — now degraded
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded})

	h.probeN(4) // sustained failure must NOT re-broadcast (exactly once per state change)
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded})
}

func TestSingleBlipDoesNotDegrade(t *testing.T) {
	p := &fakeProber{}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 2, RecoverThreshold: 1})

	h.probe()        // healthy
	p.setFail(true)  // one blip...
	h.probe()        // consecFail = 1 (< 2)
	p.setFail(false) // ...recovered before the threshold
	h.probe()        // streak reset

	if seq := h.rec.sequence(); len(seq) != 0 {
		t.Fatalf("a single failed probe must not flip status, got %v", seq)
	}
}

func TestRecoversOnlyAfterThresholdConsecutiveSuccesses(t *testing.T) {
	p := &fakeProber{fail: true}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 1, RecoverThreshold: 3})

	h.probe() // fail threshold 1 => degraded immediately
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded})

	p.setFail(false)
	h.probeN(2) // two successes — still below the recover threshold
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded})
	h.probe() // third consecutive success — recovered
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded, StatusHealthy})
}

func TestBlipDuringRecoveryResetsTheStreak(t *testing.T) {
	p := &fakeProber{fail: true}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 1, RecoverThreshold: 3})

	h.probe() // degraded
	p.setFail(false)
	h.probeN(2)     // 2 successes toward recovery
	p.setFail(true) // blip resets the recovery streak
	h.probe()       // still degraded, streak now 0
	p.setFail(false)
	h.probeN(2) // only 2 successes since the blip — not enough
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded})
	h.probe() // third — now recovered
	assertSeq(t, h.rec.sequence(), []string{StatusDegraded, StatusHealthy})
}

func TestFullOutageAndRecoveryCycle(t *testing.T) {
	p := &fakeProber{}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 2, RecoverThreshold: 2})

	h.probeN(3) // healthy, silent
	p.setFail(true)
	h.probeN(2) // degraded (2 consecutive failures)
	p.setFail(false)
	h.probeN(2) // healthy again (2 consecutive successes)
	p.setFail(true)
	h.probeN(2) // degraded again — a second full cycle broadcasts again

	assertSeq(t, h.rec.sequence(), []string{StatusDegraded, StatusHealthy, StatusDegraded})
}

func TestSeededDegradedRecoversWithoutASpuriousDegradedBroadcast(t *testing.T) {
	// Brain was down at boot: the monitor starts degraded (matching the hub's seeded status). Probes then
	// succeed; it must recover to healthy and never emit a redundant "degraded" for a state it's already in.
	p := &fakeProber{}
	h := newHarness(t, p, Config{Interval: interval, FailThreshold: 2, RecoverThreshold: 2, InitialStatus: StatusDegraded})

	h.probeN(2) // two successes from the seeded degraded state -> recover
	assertSeq(t, h.rec.sequence(), []string{StatusHealthy})

	p.setFail(true)
	h.probeN(2) // and it can degrade again on a real outage
	assertSeq(t, h.rec.sequence(), []string{StatusHealthy, StatusDegraded})
}

func TestProbeTimeoutIsPassedToTheProber(t *testing.T) {
	var gotDeadline bool
	p := proberFunc(func(ctx context.Context) error {
		_, gotDeadline = ctx.Deadline()
		return nil
	})
	h := newHarness(t, p, Config{Interval: interval, ProbeTimeout: 500 * time.Millisecond, FailThreshold: 1, RecoverThreshold: 1})
	h.probe()
	if !gotDeadline {
		t.Fatal("probe context should carry the ProbeTimeout deadline")
	}
}

type proberFunc func(context.Context) error

func (f proberFunc) Probe(ctx context.Context) error { return f(ctx) }
