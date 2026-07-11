// Package config loads and validates the engine's runtime configuration from the environment.
package config

import (
	"fmt"
	"log/slog"
	"os"
	"strconv"
	"strings"
	"time"
)

// Config is the engine's validated runtime configuration.
type Config struct {
	HTTPAddr         string        // health + WebSocket listener, e.g. ":8090"
	GRPCAddr         string        // EngineService listener, e.g. ":50071"
	BrainGRPCAddress string        // brain's quant gRPC endpoint, e.g. "brain:50061"
	BrainTimeout     time.Duration // per-call deadline for brain RPCs
	DemoCommitmentID string        // the commitment whose curve is fetched at startup
	LogLevel         slog.Level
}

// Load reads the configuration from the environment and validates it. A missing brain address or an
// out-of-range timeout is a hard error, so the process fails fast at boot rather than at first use.
func Load() (Config, error) {
	brain := os.Getenv("BRAIN_GRPC_ADDRESS")
	if brain == "" {
		return Config{}, fmt.Errorf("BRAIN_GRPC_ADDRESS is required")
	}

	timeoutMs, err := intEnv("BRAIN_TIMEOUT_MS", 3000)
	if err != nil {
		return Config{}, err
	}
	if timeoutMs < 100 || timeoutMs > 60000 {
		return Config{}, fmt.Errorf("BRAIN_TIMEOUT_MS must be between 100 and 60000, got %d", timeoutMs)
	}

	return Config{
		HTTPAddr:         env("ENGINE_HTTP_ADDR", ":8090"),
		GRPCAddr:         env("ENGINE_GRPC_ADDR", ":50071"),
		BrainGRPCAddress: brain,
		BrainTimeout:     time.Duration(timeoutMs) * time.Millisecond,
		DemoCommitmentID: env("DEMO_COMMITMENT_ID", "1"),
		LogLevel:         level(env("LOG_LEVEL", "info")),
	}, nil
}

func env(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func intEnv(key string, fallback int) (int, error) {
	raw := os.Getenv(key)
	if raw == "" {
		return fallback, nil
	}
	v, err := strconv.Atoi(raw)
	if err != nil {
		return 0, fmt.Errorf("%s must be an integer, got %q", key, raw)
	}
	return v, nil
}

func level(s string) slog.Level {
	switch strings.ToLower(s) {
	case "debug":
		return slog.LevelDebug
	case "warn":
		return slog.LevelWarn
	case "error":
		return slog.LevelError
	default:
		return slog.LevelInfo
	}
}
