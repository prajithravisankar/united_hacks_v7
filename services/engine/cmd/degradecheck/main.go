// Command degradecheck is the on-camera resilience rehearsal, automated. It drives the exact demo beat and
// asserts it: start the replay, watch the WebSocket stream, kill brain, and prove the engine broadcasts
// "degraded" while the tick stream keeps flowing uninterrupted (served from the cached curve) — then restart
// brain and prove it broadcasts "healthy" again. Optionally it also checks the ledger's valuation endpoint
// degrades (200 + degraded:true) and recovers under the same outage. It repeats the cycle N times on one
// persistent WebSocket connection, so a passing run also proves no client is ever disconnected.
//
// This is a test harness (invoked by scripts/test_degradation.sh), not part of the shipped engine image —
// the Dockerfile builds only ./cmd/engine.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/health"
	"boys/engine/internal/hub"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func main() {
	engineHTTP := flag.String("engine-http", "127.0.0.1:8090", "engine HTTP host:port (WebSocket + health)")
	engineGRPC := flag.String("engine-grpc", "127.0.0.1:50071", "engine gRPC host:port (EngineService)")
	brainHTTP := flag.String("brain-http", "127.0.0.1:8081", "brain HTTP host:port (health)")
	ledgerHTTP := flag.String("ledger-http", "127.0.0.1:8080", "ledger HTTP host:port (REST)")
	goal := flag.String("goal", "1", "commitment id the engine replays / the WS subscribes to")
	ledgerGoal := flag.Int("ledger-goal", 0, "commitment id for the ledger valuation re-verify (0 = skip)")
	brainContainer := flag.String("brain-container", "boys-brain", "brain container to kill/restart")
	speed := flag.Float64("speed", 5, "replay speed multiplier (slow enough that ticks span every cycle)")
	cycles := flag.Int("cycles", 3, "how many kill/restart cycles to run on one connection")
	flag.Parse()

	c := &checker{
		engineHTTP: *engineHTTP, engineGRPC: *engineGRPC, brainHTTP: *brainHTTP, ledgerHTTP: *ledgerHTTP,
		goal: *goal, ledgerGoal: *ledgerGoal, brainContainer: *brainContainer, speed: *speed, cycles: *cycles,
	}
	if err := c.run(); err != nil {
		fmt.Printf("\nFAIL: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("\nPASS: degradation beat survived %d kill/restart cycle(s) — degraded+recovery broadcast, ticks uninterrupted, no client dropped.\n", *cycles)
}

type checker struct {
	engineHTTP, engineGRPC, brainHTTP, ledgerHTTP string
	goal, brainContainer                          string
	ledgerGoal, cycles                            int
	speed                                         float64

	// Observer state, written by the reader goroutine.
	maxPos   int64
	statusCh chan string
	readErr  atomic.Pointer[error]
}

func step(format string, args ...any) { fmt.Printf("→ "+format+"\n", args...) }

func (c *checker) run() error {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// 1. Start the replay.
	step("starting replay for goal %s at %gx", c.goal, c.speed)
	grpcConn, err := grpc.NewClient(c.engineGRPC, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return fmt.Errorf("dial engine gRPC: %w", err)
	}
	defer grpcConn.Close()
	engine := enginev1.NewEngineServiceClient(grpcConn)
	rpcCtx, rpcCancel := context.WithTimeout(ctx, 5*time.Second)
	state, err := engine.StartReplay(rpcCtx, &enginev1.StartReplayRequest{CommitmentId: c.goal, Speed: c.speed})
	rpcCancel()
	if err != nil {
		return fmt.Errorf("StartReplay: %w", err)
	}
	if !state.GetRunning() {
		return fmt.Errorf("replay did not start running (position %d) — is the curve loaded?", state.GetPosition())
	}

	// 2. Connect the WebSocket and start observing.
	step("connecting WebSocket %s/ws/live?goal=%s", c.engineHTTP, c.goal)
	dialCtx, dialCancel := context.WithTimeout(ctx, 5*time.Second)
	conn, _, err := websocket.Dial(dialCtx, "ws://"+c.engineHTTP+"/ws/live?goal="+c.goal, nil)
	dialCancel()
	if err != nil {
		return fmt.Errorf("dial WebSocket: %w", err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(1 << 20)
	c.statusCh = make(chan string, 32)
	var wg sync.WaitGroup
	wg.Add(1)
	go func() { defer wg.Done(); c.readLoop(ctx, conn) }()
	defer wg.Wait()
	defer cancel()

	// 3. Prove ticks are flowing before we touch anything.
	step("confirming ticks flow (healthy)")
	if err := c.waitForTickAdvance(-1, 15*time.Second); err != nil {
		return fmt.Errorf("no ticks before outage: %w", err)
	}

	for i := 1; i <= c.cycles; i++ {
		fmt.Printf("\n=== cycle %d/%d ===\n", i, c.cycles)
		if err := c.oneCycle(ctx); err != nil {
			return fmt.Errorf("cycle %d: %w", i, err)
		}
	}

	if ep := c.readErr.Load(); ep != nil {
		return fmt.Errorf("the WebSocket client was disconnected during the test: %w", *ep)
	}
	return nil
}

func (c *checker) oneCycle(ctx context.Context) error {
	c.drainStatus()
	posBefore := atomic.LoadInt64(&c.maxPos)

	// Kill brain.
	step("docker kill %s", c.brainContainer)
	if err := dockerCmd(ctx, "kill", c.brainContainer); err != nil {
		return fmt.Errorf("docker kill: %w", err)
	}

	// The engine must broadcast degraded...
	if err := c.waitForStatus(health.StatusDegraded, 20*time.Second); err != nil {
		return fmt.Errorf("expected a degraded broadcast after brain died: %w", err)
	}
	step("engine broadcast status=degraded")

	// ...while the tick stream keeps advancing (replay served from the cached curve).
	if err := c.waitForTickAdvance(posBefore, 10*time.Second); err != nil {
		return fmt.Errorf("ticks stalled during the outage (replay was NOT uninterrupted): %w", err)
	}
	step("ticks kept advancing through the outage (past position %d)", posBefore)

	// The ledger degrades the same outage to 200 + degraded:true (never a 500).
	if c.ledgerGoal > 0 {
		if err := c.assertLedgerDegraded(ctx, true); err != nil {
			return fmt.Errorf("ledger valuation during outage: %w", err)
		}
		step("ledger /valuation returned 200 degraded:true")
	}

	// Restart brain and wait until it is serving again.
	step("docker start %s", c.brainContainer)
	if err := dockerCmd(ctx, "start", c.brainContainer); err != nil {
		return fmt.Errorf("docker start: %w", err)
	}
	if err := c.waitBrainHealthy(ctx, 90*time.Second); err != nil {
		return fmt.Errorf("brain did not become healthy after restart: %w", err)
	}
	step("brain is healthy again")

	// The engine must broadcast healthy...
	if err := c.waitForStatus(health.StatusHealthy, 30*time.Second); err != nil {
		return fmt.Errorf("expected a healthy broadcast after brain recovered: %w", err)
	}
	step("engine broadcast status=healthy")

	// ...and the ledger valuation is whole again.
	if c.ledgerGoal > 0 {
		if err := c.assertLedgerDegraded(ctx, false); err != nil {
			return fmt.Errorf("ledger valuation after recovery: %w", err)
		}
		step("ledger /valuation returned 200 degraded:false")
	}
	return nil
}

// readLoop is the single WebSocket reader: it records the highest tick position seen and forwards status
// changes. An error while the test is still running means the client was dropped — recorded and asserted.
func (c *checker) readLoop(ctx context.Context, conn *websocket.Conn) {
	for {
		var m hub.Message
		if err := wsjson.Read(ctx, conn, &m); err != nil {
			if ctx.Err() == nil {
				e := err
				c.readErr.CompareAndSwap(nil, &e)
			}
			return
		}
		switch m.Type {
		case "tick":
			for {
				cur := atomic.LoadInt64(&c.maxPos)
				if int64(m.Position) <= cur || atomic.CompareAndSwapInt64(&c.maxPos, cur, int64(m.Position)) {
					break
				}
			}
		case "status":
			select {
			case c.statusCh <- m.Status:
			default:
			}
		}
	}
}

func (c *checker) drainStatus() {
	for {
		select {
		case <-c.statusCh:
		default:
			return
		}
	}
}

func (c *checker) waitForStatus(want string, timeout time.Duration) error {
	deadline := time.After(timeout)
	for {
		select {
		case s := <-c.statusCh:
			if s == want {
				return nil
			}
		case <-deadline:
			return fmt.Errorf("timed out after %s waiting for status=%q", timeout, want)
		}
		if ep := c.readErr.Load(); ep != nil {
			return fmt.Errorf("connection dropped: %w", *ep)
		}
	}
}

// waitForTickAdvance returns once a tick with a position strictly greater than floor has been observed.
func (c *checker) waitForTickAdvance(floor int64, timeout time.Duration) error {
	deadline := time.After(timeout)
	tick := time.NewTicker(50 * time.Millisecond)
	defer tick.Stop()
	for {
		if atomic.LoadInt64(&c.maxPos) > floor {
			return nil
		}
		select {
		case <-tick.C:
			if ep := c.readErr.Load(); ep != nil {
				return fmt.Errorf("connection dropped: %w", *ep)
			}
		case <-deadline:
			return fmt.Errorf("timed out after %s; position stuck at %d", timeout, atomic.LoadInt64(&c.maxPos))
		}
	}
}

func (c *checker) assertLedgerDegraded(ctx context.Context, want bool) error {
	url := fmt.Sprintf("http://%s/api/goals/%d/valuation", c.ledgerHTTP, c.ledgerGoal)
	reqCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(reqCtx, http.MethodGet, url, nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return fmt.Errorf("GET %s: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s = HTTP %d, want 200 (brain outage must never 500)", url, resp.StatusCode)
	}
	var body struct {
		Degraded bool `json:"degraded"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return fmt.Errorf("decode valuation: %w", err)
	}
	if body.Degraded != want {
		return fmt.Errorf("valuation degraded=%v, want %v", body.Degraded, want)
	}
	return nil
}

// waitBrainHealthy polls brain's readiness probe until it reports ready.
func (c *checker) waitBrainHealthy(ctx context.Context, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	url := "http://" + c.brainHTTP + "/health/ready"
	for {
		reqCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
		req, _ := http.NewRequestWithContext(reqCtx, http.MethodGet, url, nil)
		resp, err := http.DefaultClient.Do(req)
		cancel()
		if err == nil {
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return nil
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("timed out after %s", timeout)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

func dockerCmd(ctx context.Context, args ...string) error {
	cmdCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	cmd := exec.CommandContext(cmdCtx, "docker", args...)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("docker %s: %w (%s)", strings.Join(args, " "), err, strings.TrimSpace(string(out)))
	}
	return nil
}
