package config

import (
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
