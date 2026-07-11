// Package httpapi serves the engine's HTTP surface: health probes now, the WebSocket live stream in B19.
package httpapi

import (
	"encoding/json"
	"net/http"
)

// Router builds the HTTP handler. Readiness reflects whether the engine has its curve loaded (ready may be
// nil, meaning always ready). Liveness is up whenever the process is.
func Router(ready func() bool) *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/health/live", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "live"})
	})
	mux.HandleFunc("/health/ready", func(w http.ResponseWriter, _ *http.Request) {
		if ready == nil || ready() {
			writeJSON(w, http.StatusOK, map[string]string{"status": "ready"})
			return
		}
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"status": "not ready"})
	})
	return mux
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}
