package httpapi

import (
	"net/http"
	"strconv"

	"boys/engine/internal/replay"
)

// RunningSink is notified when the replay's running state changes so the hub's WS snapshots/status report the
// right running flag (mirrors enginesvc's sink). The hub satisfies it via SetRunning.
type RunningSink interface{ SetRunning(bool) }

// RegisterReplayControl adds browser-drivable replay controls under /control/* (the board presses play/pause/
// speed here; the gRPC EngineService stays the canonical API). Everything mutates through the ticker's
// thread-safe command API and echoes the resulting state as JSON.
func RegisterReplayControl(mux *http.ServeMux, ticker *replay.Ticker, sink RunningSink) {
	state := func(w http.ResponseWriter) {
		st := ticker.State()
		writeJSON(w, http.StatusOK, map[string]any{
			"running":  st.Running,
			"speed":    st.Speed,
			"position": st.Position,
			"date":     st.CurrentDate,
			"done":     st.Done,
		})
	}
	notify := func() {
		if sink != nil {
			sink.SetRunning(ticker.State().Running)
		}
	}

	mux.HandleFunc("/control/start", func(w http.ResponseWriter, r *http.Request) {
		ticker.Start(speedParam(r, 30))
		notify()
		state(w)
	})
	mux.HandleFunc("/control/restart", func(w http.ResponseWriter, r *http.Request) {
		ticker.Seek(0) // rewind to the start so the fund climbs from the beginning
		ticker.Start(speedParam(r, 30))
		notify()
		state(w)
	})
	mux.HandleFunc("/control/pause", func(w http.ResponseWriter, r *http.Request) {
		ticker.Pause()
		notify()
		state(w)
	})
	mux.HandleFunc("/control/speed", func(w http.ResponseWriter, r *http.Request) {
		ticker.SetSpeed(speedParam(r, 0))
		notify()
		state(w)
	})
	mux.HandleFunc("/control/state", func(w http.ResponseWriter, _ *http.Request) {
		state(w)
	})
}

// speedParam reads ?speed= as a positive multiplier, falling back to def (a non-positive speed is ignored by
// the ticker anyway).
func speedParam(r *http.Request, def float64) float64 {
	if s := r.URL.Query().Get("speed"); s != "" {
		if v, err := strconv.ParseFloat(s, 64); err == nil && v > 0 {
			return v
		}
	}
	return def
}
