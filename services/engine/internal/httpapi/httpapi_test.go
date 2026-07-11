package httpapi

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestLivenessIsAlwaysOK(t *testing.T) {
	rec := httptest.NewRecorder()
	Router(nil).ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/health/live", nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("live = %d, want 200", rec.Code)
	}
}

func TestReadinessReflectsTheReadyFunc(t *testing.T) {
	ready := false
	router := Router(func() bool { return ready })

	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/health/ready", nil))
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("not-ready = %d, want 503", rec.Code)
	}

	ready = true
	rec = httptest.NewRecorder()
	router.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/health/ready", nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("ready = %d, want 200", rec.Code)
	}
}

// The B17 acceptance: a server under concurrent load shuts down cleanly, leaking no goroutines (goleak
// verifies in TestMain).
func TestServerShutsDownUnderLoadWithNoLeaks(t *testing.T) {
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	srv := &http.Server{Handler: Router(nil)}
	go func() { _ = srv.Serve(lis) }()

	client := &http.Client{Transport: &http.Transport{DisableKeepAlives: true}}
	url := "http://" + lis.Addr().String() + "/health/live"

	var wg sync.WaitGroup
	for i := 0; i < 25; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if resp, err := client.Get(url); err == nil {
				_ = resp.Body.Close()
			}
		}()
	}
	wg.Wait()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := srv.Shutdown(ctx); err != nil {
		t.Fatalf("shutdown: %v", err)
	}
	client.CloseIdleConnections()
}
