// Package hub fans out the replay tick stream to every connected WebSocket client. A single broadcast
// goroutine owns the client set (no per-message locking); each client has its own buffered send channel and
// is dropped — never blocks the hub — if it can't keep up. Late joiners get a snapshot first, then only
// subsequent ticks. This is Go's headline: correct concurrent fan-out.
package hub

import (
	"context"
	"net/http"
	"time"

	"boys/engine/internal/replay"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

// CloseUnknownGoal is the WebSocket close code sent when a client connects for a goal the engine doesn't
// serve (documented in docs/ws-contract.md).
const CloseUnknownGoal = websocket.StatusCode(4404)

const writeTimeout = 5 * time.Second

// Message is one frame on the wire. NAV is always integer cents. See docs/ws-contract.md.
type Message struct {
	Type     string   `json:"type"` // "snapshot" | "tick" | "status"
	Position int      `json:"position"`
	Date     string   `json:"date,omitempty"`
	NavCents int64    `json:"navCents"`
	Events   []string `json:"events,omitempty"`
	Running  bool     `json:"running"`
	Terminal bool     `json:"terminal,omitempty"`
	Status   string   `json:"status,omitempty"` // for status messages: "healthy" | "degraded"
}

type client struct {
	conn   *websocket.Conn
	send   chan Message
	ctx    context.Context
	cancel context.CancelFunc // cancelled to tear the client down (drop, disconnect, or shutdown)
}

// Hub broadcasts the ticker's stream to WebSocket clients for one commitment.
type Hub struct {
	ticker       *replay.Ticker
	commitmentID string
	sendBuffer   int

	register   chan *client
	unregister chan *client
	statusCh   chan string
	done       chan struct{}

	// Broadcast-goroutine-owned state (touched ONLY inside Run — never locked, never shared).
	lastTick      replay.Tick
	lastTickValid bool
	status        string
}

// NewHub builds a hub. sendBuffer is the per-client queue depth before a slow client is dropped.
func NewHub(ticker *replay.Ticker, commitmentID string, sendBuffer int) *Hub {
	if sendBuffer < 1 {
		sendBuffer = 1
	}
	return &Hub{
		ticker:       ticker,
		commitmentID: commitmentID,
		sendBuffer:   sendBuffer,
		register:     make(chan *client),
		unregister:   make(chan *client),
		statusCh:     make(chan string),
		done:         make(chan struct{}),
		status:       "healthy",
	}
}

// Run is the single broadcast goroutine. It owns the client set and the tick stream. Exits (and tears down
// all clients) on context cancel or when the ticker's stream closes.
func (h *Hub) Run(ctx context.Context) {
	defer close(h.done)
	clients := map[*client]struct{}{}
	defer func() {
		for c := range clients {
			c.cancel()
		}
	}()

	for {
		select {
		case <-ctx.Done():
			return
		case c := <-h.register:
			clients[c] = struct{}{}
			h.deliver(clients, c, h.snapshotMessage()) // snapshot first, before any subsequent tick
		case c := <-h.unregister:
			if _, ok := clients[c]; ok {
				delete(clients, c)
				c.cancel()
			}
		case tick, ok := <-h.ticker.Ticks():
			if !ok {
				return // ticker stream closed -> shut down
			}
			h.lastTick = tick
			h.lastTickValid = true
			h.broadcast(clients, tickMessage(tick))
		case status := <-h.statusCh:
			h.status = status
			h.broadcast(clients, Message{Type: "status", Status: status, Running: h.lastTickValid && !h.lastTick.Terminal})
		}
	}
}

// broadcast sends to every client without ever blocking; a client whose buffer is full is dropped.
func (h *Hub) broadcast(clients map[*client]struct{}, msg Message) {
	for c := range clients {
		h.deliver(clients, c, msg)
	}
}

func (h *Hub) deliver(clients map[*client]struct{}, c *client, msg Message) {
	select {
	case c.send <- msg:
	default: // buffer full — the client can't keep up; drop it (cancel aborts any in-flight write at once)
		delete(clients, c)
		c.cancel()
	}
}

func (h *Hub) snapshotMessage() Message {
	if !h.lastTickValid {
		return Message{Type: "snapshot", Position: 0, NavCents: 0, Running: false, Status: h.status}
	}
	return Message{
		Type:     "snapshot",
		Position: h.lastTick.Position,
		Date:     h.lastTick.Date,
		NavCents: h.lastTick.NavCents,
		Running:  !h.lastTick.Terminal,
		Status:   h.status,
	}
}

func tickMessage(t replay.Tick) Message {
	return Message{
		Type:     "tick",
		Position: t.Position,
		Date:     t.Date,
		NavCents: t.NavCents,
		Events:   t.Events,
		Terminal: t.Terminal,
		Running:  !t.Terminal,
	}
}

// BroadcastStatus pushes a status change (e.g. "degraded") to every client. Used by B20.
func (h *Hub) BroadcastStatus(status string) {
	select {
	case h.statusCh <- status:
	case <-h.done:
	}
}

// Handler upgrades a request to a WebSocket and joins it to the broadcast. Clients are read-only in v0;
// inbound frames are discarded and protocol errors close the connection cleanly.
func (h *Hub) Handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		conn, err := websocket.Accept(w, r, nil)
		if err != nil {
			return
		}

		if r.URL.Query().Get("goal") != h.commitmentID {
			_ = conn.Close(CloseUnknownGoal, "unknown goal")
			return
		}

		// ctx is the single teardown signal: cancelled when the hub drops us, the engine shuts down, the
		// client disconnects, or the handler returns.
		ctx, cancel := context.WithCancel(context.Background())
		defer cancel()
		c := &client{conn: conn, send: make(chan Message, h.sendBuffer), ctx: ctx, cancel: cancel}

		// Read pump: clients are read-only, so we just drain and discard inbound frames. Any read error
		// (disconnect) triggers teardown. Reads use a background context so they never invoke the library's
		// graceful close — the one and only close is the forceful CloseNow below.
		go func() {
			for {
				if _, _, err := conn.Read(context.Background()); err != nil {
					cancel()
					return
				}
			}
		}()

		// Closer: exactly one forceful close on teardown, which aborts a blocked read/write at once and
		// releases the connection's goroutines (CloseNow skips the handshake, so it never hangs).
		go func() {
			<-ctx.Done()
			_ = conn.CloseNow()
		}()

		select {
		case h.register <- c:
		case <-h.done:
			return
		}

		h.writeLoop(c)

		select {
		case h.unregister <- c:
		case <-h.done:
		}
	}
}

// writeLoop drains the client's send channel to the socket until its context is cancelled (client
// disconnected, hub dropped it, or the engine is shutting down) or a write fails. A blocked write is
// aborted by the closer's CloseNow, not by cancelling the write's own context (which would trigger the
// library's graceful-close path).
func (h *Hub) writeLoop(c *client) {
	for {
		select {
		case <-c.ctx.Done():
			return
		case msg := <-c.send:
			wctx, cancel := context.WithTimeout(context.Background(), writeTimeout)
			err := wsjson.Write(wctx, c.conn, msg)
			cancel()
			if err != nil {
				return
			}
		}
	}
}
