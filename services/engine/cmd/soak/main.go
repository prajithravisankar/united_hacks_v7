// Command soak is the R5 endurance test: it holds N WebSocket clients open against the engine, keeps the
// replay looping at 30×, and kills/restarts brain on a cycle — sampling the engine's goroutine count and heap
// (via pprof) throughout. A pass means resource usage stays flat (no goroutine or memory leak) and no client
// is ever dropped, across the whole window. Requires the engine to be started with ENGINE_ENABLE_PPROF=1.
//
// This is a review/test harness (invoked from the R5 review); the Dockerfile builds only ./cmd/engine.
package main

import (
	"bufio"
	"context"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	enginev1 "boys/engine/gen/boys/engine/v1"
	"boys/engine/internal/hub"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func main() {
	duration := flag.Duration("duration", 30*time.Minute, "total soak duration")
	clients := flag.Int("clients", 20, "concurrent WebSocket clients")
	killEvery := flag.Duration("kill-every", 3*time.Minute, "brain kill/restart period")
	brainDown := flag.Duration("brain-down", 20*time.Second, "how long brain stays killed each cycle")
	sampleEvery := flag.Duration("sample-every", 30*time.Second, "resource sampling period")
	speed := flag.Float64("speed", 30, "replay speed multiplier")
	restartEvery := flag.Duration("restart-replay-every", 5*time.Second, "how often to (re)issue StartReplay so it loops")
	engineHTTP := flag.String("engine-http", "127.0.0.1:8090", "engine HTTP host:port")
	engineGRPC := flag.String("engine-grpc", "127.0.0.1:50071", "engine gRPC host:port")
	brainHTTP := flag.String("brain-http", "127.0.0.1:8081", "brain HTTP host:port")
	brainContainer := flag.String("brain-container", "boys-brain", "brain container to kill/restart")
	goal := flag.String("goal", "1", "commitment id")
	out := flag.String("out", "", "optional CSV samples file")
	flag.Parse()

	s := &soak{
		duration: *duration, clients: *clients, killEvery: *killEvery, brainDown: *brainDown,
		sampleEvery: *sampleEvery, speed: *speed, restartEvery: *restartEvery,
		engineHTTP: *engineHTTP, engineGRPC: *engineGRPC, brainHTTP: *brainHTTP,
		brainContainer: *brainContainer, goal: *goal, outPath: *out,
	}
	if err := s.run(); err != nil {
		fmt.Printf("\nFAIL: %v\n", err)
		os.Exit(1)
	}
}

type soak struct {
	duration, killEvery, brainDown, sampleEvery, restartEvery time.Duration
	clients                                                   int
	speed                                                     float64
	engineHTTP, engineGRPC, brainHTTP, brainContainer, goal   string
	outPath                                                   string

	tickTotals []int64 // per-client tick counts (atomic)
	clientErr  atomic.Pointer[error]
	kills      atomic.Int64
	samples    []sample
}

type sample struct {
	offset     time.Duration
	goroutines int
	heapMB     float64
	sysMB      float64
	ticks      int64
}

func (s *soak) run() error {
	ctx, cancel := context.WithTimeout(context.Background(), s.duration)
	defer cancel()

	// Preflight: pprof must be enabled, or we can't measure anything.
	if _, err := s.goroutines(ctx); err != nil {
		return fmt.Errorf("pprof not reachable at %s/debug/pprof/ — start the engine with ENGINE_ENABLE_PPROF=1: %w", s.engineHTTP, err)
	}

	// Start the replay.
	gc, err := grpc.NewClient(s.engineGRPC, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return fmt.Errorf("dial engine gRPC: %w", err)
	}
	defer gc.Close()
	engine := enginev1.NewEngineServiceClient(gc)
	if err := s.startReplay(ctx, engine); err != nil {
		return fmt.Errorf("initial StartReplay: %w", err)
	}

	// Connect the client fleet.
	s.tickTotals = make([]int64, s.clients)
	var wg sync.WaitGroup
	for i := 0; i < s.clients; i++ {
		conn, _, err := websocket.Dial(ctx, "ws://"+s.engineHTTP+"/ws/live?goal="+s.goal, nil)
		if err != nil {
			return fmt.Errorf("client %d dial: %w", i, err)
		}
		conn.SetReadLimit(1 << 20)
		wg.Add(1)
		go func(i int, c *websocket.Conn) {
			defer wg.Done()
			s.readClient(ctx, i, c)
		}(i, conn)
	}
	fmt.Printf("soak: %d clients connected; duration %s; kill every %s; sampling every %s\n",
		s.clients, s.duration, s.killEvery, s.sampleEvery)

	// Background drivers: keep the replay looping, kill/restart brain, sample resources.
	var drivers sync.WaitGroup
	drivers.Add(3)
	go func() { defer drivers.Done(); s.replayLoop(ctx, engine) }()
	go func() { defer drivers.Done(); s.killLoop(ctx) }()
	start := time.Now()
	go func() { defer drivers.Done(); s.sampleLoop(ctx, start) }()

	<-ctx.Done()   // run until the duration elapses
	drivers.Wait() // stop the drivers
	cancel()       // ensure clients unblock
	wg.Wait()      // wait for client readers to exit

	return s.report()
}

func (s *soak) readClient(ctx context.Context, i int, c *websocket.Conn) {
	defer c.CloseNow()
	for {
		var m hub.Message
		if err := wsjson.Read(ctx, c, &m); err != nil {
			if ctx.Err() == nil { // an error before the soak ends means the client was dropped — a failure
				e := fmt.Errorf("client %d dropped mid-soak: %w", i, err)
				s.clientErr.CompareAndSwap(nil, &e)
			}
			return
		}
		if m.Type == "tick" {
			atomic.AddInt64(&s.tickTotals[i], 1)
		}
	}
}

func (s *soak) replayLoop(ctx context.Context, engine enginev1.EngineServiceClient) {
	t := time.NewTicker(s.restartEvery)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			_ = s.startReplay(ctx, engine) // restarts from 0 once the previous run finished; keeps ticks flowing
		}
	}
}

func (s *soak) startReplay(ctx context.Context, engine enginev1.EngineServiceClient) error {
	rc, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	_, err := engine.StartReplay(rc, &enginev1.StartReplayRequest{CommitmentId: s.goal, Speed: s.speed})
	return err
}

func (s *soak) killLoop(ctx context.Context) {
	t := time.NewTicker(s.killEvery)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			if err := docker(ctx, "kill", s.brainContainer); err != nil {
				fmt.Printf("  [kill] %v\n", err)
				continue
			}
			s.kills.Add(1)
			fmt.Printf("  [kill] brain down (cycle %d)\n", s.kills.Load())
			select {
			case <-ctx.Done():
				return
			case <-time.After(s.brainDown):
			}
			if err := docker(ctx, "start", s.brainContainer); err != nil {
				fmt.Printf("  [kill] restart: %v\n", err)
				continue
			}
			s.waitBrainHealthy(ctx)
			fmt.Printf("  [kill] brain back up\n")
		}
	}
}

func (s *soak) sampleLoop(ctx context.Context, start time.Time) {
	t := time.NewTicker(s.sampleEvery)
	defer t.Stop()
	s.takeSample(ctx, start) // baseline
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			s.takeSample(ctx, start)
		}
	}
}

func (s *soak) takeSample(ctx context.Context, start time.Time) {
	g, err := s.goroutines(ctx)
	if err != nil {
		return // brain-kill windows can briefly perturb sampling; skip rather than abort
	}
	heap, sys := s.heap(ctx)
	var ticks int64
	for i := range s.tickTotals {
		ticks += atomic.LoadInt64(&s.tickTotals[i])
	}
	smp := sample{offset: time.Since(start).Round(time.Second), goroutines: g, heapMB: heap, sysMB: sys, ticks: ticks}
	s.samples = append(s.samples, smp)
	fmt.Printf("  [sample %5s] goroutines=%d heapAlloc=%.1fMB sys=%.1fMB ticks=%d\n",
		smp.offset, smp.goroutines, smp.heapMB, smp.sysMB, smp.ticks)
}

func (s *soak) goroutines(ctx context.Context) (int, error) {
	body, err := s.get(ctx, "/debug/pprof/goroutine?debug=1")
	if err != nil {
		return 0, err
	}
	// First line: "goroutine profile: total N"
	line, _, _ := strings.Cut(body, "\n")
	fields := strings.Fields(line)
	if len(fields) == 0 {
		return 0, fmt.Errorf("unexpected goroutine profile: %q", line)
	}
	return strconv.Atoi(fields[len(fields)-1])
}

func (s *soak) heap(ctx context.Context) (heapMB, sysMB float64) {
	body, err := s.get(ctx, "/debug/pprof/heap?debug=1")
	if err != nil {
		return 0, 0
	}
	sc := bufio.NewScanner(strings.NewReader(body))
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		switch {
		case strings.HasPrefix(line, "# HeapAlloc = "):
			heapMB = bytesToMB(line)
		case strings.HasPrefix(line, "# Sys = "):
			sysMB = bytesToMB(line)
		}
	}
	return heapMB, sysMB
}

func bytesToMB(line string) float64 {
	parts := strings.Fields(line)
	if len(parts) == 0 {
		return 0
	}
	n, _ := strconv.ParseFloat(parts[len(parts)-1], 64)
	return n / (1024 * 1024)
}

func (s *soak) get(ctx context.Context, path string) (string, error) {
	rc, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(rc, http.MethodGet, "http://"+s.engineHTTP+path, nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	sb := new(strings.Builder)
	buf := make([]byte, 32*1024)
	for {
		n, rerr := resp.Body.Read(buf)
		sb.Write(buf[:n])
		if rerr != nil {
			break
		}
	}
	return sb.String(), nil
}

func (s *soak) waitBrainHealthy(ctx context.Context) {
	deadline := time.Now().Add(90 * time.Second)
	for time.Now().Before(deadline) {
		rc, cancel := context.WithTimeout(ctx, 2*time.Second)
		req, _ := http.NewRequestWithContext(rc, http.MethodGet, "http://"+s.brainHTTP+"/health/ready", nil)
		resp, err := http.DefaultClient.Do(req)
		cancel()
		if err == nil {
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return
			}
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Second):
		}
	}
}

func docker(ctx context.Context, args ...string) error {
	cc, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	if out, err := exec.CommandContext(cc, "docker", args...).CombinedOutput(); err != nil {
		return fmt.Errorf("docker %s: %w (%s)", strings.Join(args, " "), err, strings.TrimSpace(string(out)))
	}
	return nil
}

func (s *soak) report() error {
	if s.outPath != "" {
		s.writeCSV()
	}
	if len(s.samples) < 2 {
		return fmt.Errorf("not enough samples (%d) to judge flatness", len(s.samples))
	}
	base, final := s.samples[0], s.samples[len(s.samples)-1]

	// Peak/min goroutines across the run — the strongest leak signal is the goroutine count returning to a
	// steady baseline rather than climbing.
	minG, maxG := s.samples[0].goroutines, s.samples[0].goroutines
	maxHeap := s.samples[0].heapMB
	var totalTicks int64
	for _, smp := range s.samples {
		if smp.goroutines < minG {
			minG = smp.goroutines
		}
		if smp.goroutines > maxG {
			maxG = smp.goroutines
		}
		if smp.heapMB > maxHeap {
			maxHeap = smp.heapMB
		}
	}
	for i := range s.tickTotals {
		totalTicks += atomic.LoadInt64(&s.tickTotals[i])
	}

	fmt.Printf("\n== soak summary ==\n")
	fmt.Printf("samples          : %d over %s\n", len(s.samples), final.offset)
	fmt.Printf("brain kills       : %d\n", s.kills.Load())
	fmt.Printf("goroutines        : baseline %d, final %d, min %d, max %d\n", base.goroutines, final.goroutines, minG, maxG)
	fmt.Printf("heapAlloc (MB)    : baseline %.1f, final %.1f, peak %.1f\n", base.heapMB, final.heapMB, maxHeap)
	fmt.Printf("ticks delivered   : %d across %d clients\n", totalTicks, s.clients)

	// Verdicts.
	if ep := s.clientErr.Load(); ep != nil {
		return *ep
	}
	for i := range s.tickTotals {
		if atomic.LoadInt64(&s.tickTotals[i]) == 0 {
			return fmt.Errorf("client %d received zero ticks", i)
		}
	}
	// Flatness: goroutines must not climb materially above baseline (allow headroom for in-flight handlers).
	const goroutineTolerance = 15
	if final.goroutines > base.goroutines+goroutineTolerance {
		return fmt.Errorf("goroutine count grew from %d to %d (>%d) — likely a leak", base.goroutines, final.goroutines, goroutineTolerance)
	}
	if maxG > base.goroutines+goroutineTolerance {
		return fmt.Errorf("goroutine count peaked at %d vs baseline %d (>%d) — likely a leak", maxG, base.goroutines, goroutineTolerance)
	}
	fmt.Printf("\nPASS: flat goroutines (%d±, tolerance %d), no client dropped, %d ticks delivered across %d brain kills.\n",
		base.goroutines, goroutineTolerance, totalTicks, s.kills.Load())
	return nil
}

func (s *soak) writeCSV() {
	f, err := os.Create(s.outPath)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintln(f, "offset_s,goroutines,heap_mb,sys_mb,ticks")
	for _, smp := range s.samples {
		fmt.Fprintf(f, "%d,%d,%.2f,%.2f,%d\n", int(smp.offset.Seconds()), smp.goroutines, smp.heapMB, smp.sysMB, smp.ticks)
	}
}
