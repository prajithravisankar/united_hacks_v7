package hub

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"

	"boys/engine/internal/clock"
	"boys/engine/internal/replay"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func makeTimeline(n int) []replay.Point {
	pts := make([]replay.Point, n)
	for i := range pts {
		pts[i] = replay.Point{Date: fmt.Sprintf("2021-08-%02d", i+1), NavCents: int64(10000 + i*10)}
	}
	return pts
}

// bigTimeline gives each point a ~64KB events payload, so a couple of undrained messages overflow any OS
// TCP buffer and the drop becomes deterministic (not dependent on socket buffer sizes).
func bigTimeline(n int) []replay.Point {
	big := []string{strings.Repeat("x", 64*1024)}
	pts := make([]replay.Point, n)
	for i := range pts {
		pts[i] = replay.Point{Date: fmt.Sprintf("2021-08-%02d", i+1), NavCents: int64(10000 + i), Events: big}
	}
	return pts
}

func serve(t *testing.T, ticker *replay.Ticker, buffer int) string {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	go ticker.Run(ctx)
	h := NewHub(ticker, "1", buffer)
	go h.Run(ctx)
	mux := http.NewServeMux()
	mux.HandleFunc("/ws/live", h.Handler())
	srv := httptest.NewServer(mux)
	t.Cleanup(func() {
		cancel()
		srv.Close()
	})
	return srv.URL
}

func setup(t *testing.T, clk clock.Clock, step time.Duration, n, buffer int) (string, *replay.Ticker) {
	t.Helper()
	ticker := replay.New(makeTimeline(n), clk, step)
	return serve(t, ticker, buffer), ticker
}

func dial(t *testing.T, url, goal string) *websocket.Conn {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	wsURL := strings.Replace(url, "http", "ws", 1) + "/ws/live?goal=" + goal
	conn, _, err := websocket.Dial(ctx, wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	conn.SetReadLimit(1 << 20) // the backpressure test sends deliberately large frames
	t.Cleanup(func() { _ = conn.Close(websocket.StatusNormalClosure, "") })
	return conn
}

func readErr(conn *websocket.Conn) (Message, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	var m Message
	err := wsjson.Read(ctx, conn, &m)
	return m, err
}

func read(t *testing.T, conn *websocket.Conn) Message {
	t.Helper()
	m, err := readErr(conn)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	return m
}

func mustSnapshot(t *testing.T, conn *websocket.Conn) Message {
	t.Helper()
	m := read(t, conn)
	if m.Type != "snapshot" {
		t.Fatalf("first message = %q, want snapshot", m.Type)
	}
	return m
}

func TestTwoClientsReceiveIdenticalSequences(t *testing.T) {
	url, ticker := setup(t, clock.RealClock{}, time.Millisecond, 10, 32)
	c1, c2 := dial(t, url, "1"), dial(t, url, "1")
	mustSnapshot(t, c1)
	mustSnapshot(t, c2) // both registered before we start

	ticker.Start(1)
	var s1, s2 []Message
	for i := 0; i < 10; i++ {
		s1 = append(s1, read(t, c1))
		s2 = append(s2, read(t, c2))
	}

	if !reflect.DeepEqual(s1, s2) {
		t.Fatalf("clients diverged:\n%v\n%v", s1, s2)
	}
	for i, m := range s1 {
		if m.Type != "tick" || m.Position != i {
			t.Fatalf("tick %d = %+v", i, m)
		}
	}
}

func TestLateJoinerGetsSnapshotThenOnlySubsequentTicks(t *testing.T) {
	fc := clock.NewFake()
	url, ticker := setup(t, fc, 30*time.Millisecond, 20, 32)
	c1 := dial(t, url, "1")
	mustSnapshot(t, c1)
	ticker.Start(1) // delay = 30ms

	for i := 0; i < 5; i++ { // c1 receives positions 0..4
		fc.BlockUntil(1)
		fc.Advance(30 * time.Millisecond)
		if m := read(t, c1); m.Position != i {
			t.Fatalf("c1 position %d, want %d", m.Position, i)
		}
	}

	// Position 4 is the latest emitted; a late joiner must be caught up to it, then continue.
	c2 := dial(t, url, "1")
	snap := mustSnapshot(t, c2)
	if snap.Position != 4 || snap.NavCents != 10000+4*10 {
		t.Fatalf("late snapshot = %+v, want position 4", snap)
	}

	for i := 5; i < 8; i++ {
		fc.BlockUntil(1)
		fc.Advance(30 * time.Millisecond)
		if m := read(t, c1); m.Position != i {
			t.Fatalf("c1 position %d, want %d", m.Position, i)
		}
		if m := read(t, c2); m.Position != i { // no missed, no duplicate
			t.Fatalf("late joiner position %d, want %d", m.Position, i)
		}
	}
}

func TestSlowClientIsDroppedAndOthersAreUnaffected(t *testing.T) {
	// Big messages so the drop is deterministic (a couple overflow any TCP buffer); a gentle tick rate the
	// fast client comfortably keeps up with while the non-reading slow one fills and is dropped.
	ticker := replay.New(bigTimeline(1000), clock.RealClock{}, 10*time.Millisecond)
	url := serve(t, ticker, 8)
	fast := dial(t, url, "1")
	slow := dial(t, url, "1")
	mustSnapshot(t, fast)
	mustSnapshot(t, slow) // slow reads its snapshot, then never reads again -> buffer fills -> dropped

	ticker.Start(1)
	start := time.Now()
	for i := 0; i < 40; i++ {
		read(t, fast) // fast keeps up
	}
	elapsed := time.Since(start)

	if elapsed > 2*time.Second {
		t.Fatalf("the fast client was stalled by the slow one: %v", elapsed)
	}
	// The slow client had a few messages buffered before it was dropped; drain them, then hit the close.
	dropped := false
	for i := 0; i < 500; i++ {
		if _, err := readErr(slow); err != nil {
			dropped = true
			break
		}
	}
	if !dropped {
		t.Fatal("expected the stalled client to be dropped (closed)")
	}
}

func TestHundredClientsReceiveAFullReplay(t *testing.T) {
	const clients, ticks = 100, 30
	url, ticker := setup(t, clock.RealClock{}, 500*time.Microsecond, ticks, ticks+8)
	conns := make([]*websocket.Conn, clients)
	for i := range conns {
		conns[i] = dial(t, url, "1")
		mustSnapshot(t, conns[i])
	}

	ticker.Start(1)
	errs := make([]error, clients)
	var wg sync.WaitGroup
	for i := range conns {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			for j := 0; j < ticks; j++ {
				if m, err := readErr(conns[i]); err != nil {
					errs[i] = err
					return
				} else if m.Type != "tick" {
					errs[i] = fmt.Errorf("client %d got %q", i, m.Type)
					return
				}
			}
		}(i)
	}
	wg.Wait()

	for i, err := range errs {
		if err != nil {
			t.Fatalf("client %d: %v", i, err)
		}
	}
}

func TestClientDisconnectMidStreamIsCleanedUp(t *testing.T) {
	url, ticker := setup(t, clock.RealClock{}, time.Millisecond, 200, 8)
	c := dial(t, url, "1")
	mustSnapshot(t, c)
	ticker.Start(1)
	read(t, c)

	_ = c.Close(websocket.StatusNormalClosure, "leaving") // disconnect mid-stream
	time.Sleep(50 * time.Millisecond)                     // let the hub reap it (goleak asserts no leak)
}

func TestInboundFramesAreIgnoredSafely(t *testing.T) {
	url, ticker := setup(t, clock.RealClock{}, time.Millisecond, 500, 32)
	talker := dial(t, url, "1")
	listener := dial(t, url, "1")
	mustSnapshot(t, talker)
	mustSnapshot(t, listener)
	ticker.Start(1)

	// Clients are read-only; a stray inbound frame is discarded, disrupting nothing.
	wctx, cancel := context.WithTimeout(context.Background(), time.Second)
	_ = talker.Write(wctx, websocket.MessageText, []byte("read-only, please ignore"))
	cancel()

	// Both clients keep receiving ticks — the stray frame changed nothing.
	if m := read(t, talker); m.Type != "tick" {
		t.Fatalf("talker got %q, want tick", m.Type)
	}
	if m := read(t, listener); m.Type != "tick" {
		t.Fatalf("listener got %q, want tick", m.Type)
	}
}

func TestUnknownGoalClosesWithDocumentedCode(t *testing.T) {
	url, _ := setup(t, clock.RealClock{}, time.Millisecond, 10, 8)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, strings.Replace(url, "http", "ws", 1)+"/ws/live?goal=999", nil)
	if err != nil {
		return // server rejected during handshake — also acceptable
	}
	defer conn.Close(websocket.StatusInternalError, "")

	_, _, readErr := conn.Read(ctx)
	if websocket.CloseStatus(readErr) != CloseUnknownGoal {
		t.Fatalf("close status = %v, want %v", websocket.CloseStatus(readErr), CloseUnknownGoal)
	}
}
