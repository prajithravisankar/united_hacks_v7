package config

import (
	"log/slog"
	"testing"
	"time"

	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestLoadAppliesDefaults(t *testing.T) {
	t.Setenv("BRAIN_GRPC_ADDRESS", "brain:50061")

	cfg, err := Load()
	if err != nil {
		t.Fatalf("Load() error: %v", err)
	}
	if cfg.HTTPAddr != ":8090" || cfg.GRPCAddr != ":50071" {
		t.Fatalf("unexpected default addrs: %+v", cfg)
	}
	if cfg.BrainTimeout != 3000*time.Millisecond {
		t.Fatalf("timeout = %v, want 3s", cfg.BrainTimeout)
	}
	if cfg.DemoCommitmentID != "1" {
		t.Fatalf("demo commitment = %q, want 1", cfg.DemoCommitmentID)
	}
}

func TestLoadRequiresBrainAddress(t *testing.T) {
	// no BRAIN_GRPC_ADDRESS set
	if _, err := Load(); err == nil {
		t.Fatal("expected an error when BRAIN_GRPC_ADDRESS is missing")
	}
}

func TestLoadRejectsOutOfRangeTimeout(t *testing.T) {
	t.Setenv("BRAIN_GRPC_ADDRESS", "brain:50061")
	t.Setenv("BRAIN_TIMEOUT_MS", "50") // below the 100 floor

	if _, err := Load(); err == nil {
		t.Fatal("expected an error for an out-of-range timeout")
	}
}

func TestLoadRejectsNonNumericTimeout(t *testing.T) {
	t.Setenv("BRAIN_GRPC_ADDRESS", "brain:50061")
	t.Setenv("BRAIN_TIMEOUT_MS", "soon")

	if _, err := Load(); err == nil {
		t.Fatal("expected an error for a non-numeric timeout")
	}
}

func TestLoadParsesLogLevelsAndPprof(t *testing.T) {
	t.Setenv("BRAIN_GRPC_ADDRESS", "brain:50061")
	for _, tc := range []struct {
		in   string
		want slog.Level
	}{
		{"debug", slog.LevelDebug},
		{"warn", slog.LevelWarn},
		{"error", slog.LevelError},
		{"nonsense", slog.LevelInfo}, // unknown -> info
	} {
		t.Setenv("LOG_LEVEL", tc.in)
		cfg, err := Load()
		if err != nil {
			t.Fatalf("Load(%q): %v", tc.in, err)
		}
		if cfg.LogLevel != tc.want {
			t.Fatalf("LOG_LEVEL=%q -> %v, want %v", tc.in, cfg.LogLevel, tc.want)
		}
	}

	t.Setenv("LOG_LEVEL", "info")
	if cfg, _ := Load(); cfg.EnablePprof {
		t.Fatal("pprof should default off")
	}
	t.Setenv("ENGINE_ENABLE_PPROF", "1")
	if cfg, _ := Load(); !cfg.EnablePprof {
		t.Fatal("ENGINE_ENABLE_PPROF=1 should enable pprof")
	}
}
