package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"boys/engine/internal/clock"
	"boys/engine/internal/replay"
)

func TestReplayControlDrivesTheTicker(t *testing.T) {
	tl := []replay.Point{{Date: "2021-08-13", NavCents: 10000}, {Date: "2021-08-14", NavCents: 10100}}
	ticker := replay.New(tl, clock.RealClock{}, time.Second)
	ctx, cancel := context.WithCancel(context.Background())
	go ticker.Run(ctx)
	t.Cleanup(func() {
		cancel()
		for range ticker.Ticks() {
		}
	})

	mux := http.NewServeMux()
	RegisterReplayControl(mux, ticker, nil)

	do := func(path string) map[string]any {
		rec := httptest.NewRecorder()
		mux.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, path, nil))
		if rec.Code != http.StatusOK {
			t.Fatalf("%s = %d", path, rec.Code)
		}
		var m map[string]any
		if err := json.Unmarshal(rec.Body.Bytes(), &m); err != nil {
			t.Fatalf("%s: bad json: %v", path, err)
		}
		return m
	}

	if st := do("/control/start?speed=5"); st["running"] != true || st["speed"] != 5.0 {
		t.Fatalf("after start: %v", st)
	}
	if st := do("/control/pause"); st["running"] != false {
		t.Fatalf("after pause: %v", st)
	}
}
