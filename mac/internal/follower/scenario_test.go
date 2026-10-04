package follower_test

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/jwc20/pokerland-trackers/mac/internal/api"
	"github.com/jwc20/pokerland-trackers/mac/internal/follower"
	"github.com/jwc20/pokerland-trackers/mac/internal/state"
)

// Mirrors protocol/scenarios/README.md.
type scenario struct {
	Name   string          `json:"name"`
	Config json.RawMessage `json:"config"`
	Steps  []step          `json:"steps"`
	Expect struct {
		Chunks         map[string]int `json:"chunks"`
		Streams        map[string]int `json:"streams"`
		RequestsAtMost *int           `json:"requests_at_most"`
	} `json:"expect"`
}

type step struct {
	Write    *struct{ File, Text string } `json:"write"`
	Truncate *struct {
		File string
		Size int64
	} `json:"truncate"`
	Delete    *struct{ File string } `json:"delete"`
	Tick      int                    `json:"tick"`
	Advance   int                    `json:"advance"`
	Restart   bool                   `json:"restart"`
	LoseState bool                   `json:"lose_state"`
	Server    *struct {
		Fail   int `json:"fail"`
		Status int `json:"status"`
		Times  int `json:"times"`
		Rewind *struct {
			File        string `json:"file"`
			AckedOffset int64  `json:"acked_offset"`
		} `json:"rewind"`
	} `json:"server"`
}

func TestScenarios(t *testing.T) {
	files, err := filepath.Glob(filepath.Join("..", "..", "..", "protocol", "scenarios", "*.json"))
	if err != nil || len(files) == 0 {
		t.Fatalf("no scenarios found: %v", err)
	}
	for _, file := range files {
		file := file
		t.Run(strings.TrimSuffix(filepath.Base(file), ".json"), func(t *testing.T) { runScenario(t, file) })
	}
}

func runScenario(t *testing.T, file string) {
	raw, err := os.ReadFile(file)
	if err != nil {
		t.Fatal(err)
	}
	var sc scenario
	if err := json.Unmarshal(raw, &sc); err != nil {
		t.Fatal(err)
	}
	config := api.DefaultConfig
	if sc.Config != nil {
		if err := json.Unmarshal(sc.Config, &config); err != nil {
			t.Fatal(err)
		}
	}

	root := t.TempDir()
	statePath := filepath.Join(t.TempDir(), "state.json")
	server := newFakeServer(t, config)
	clock := time.Date(2026, 10, 3, 12, 0, 0, 0, time.UTC)
	client := api.New(server.server.URL, "0123456789abcdef0123456789abcdef", follower.UserAgent("0.1.0", "macos", "arm64"))
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	if testing.Verbose() {
		logger = slog.New(slog.NewTextHandler(os.Stderr, nil))
	}

	var f *follower.Follower
	start := func() {
		store, err := state.Load(statePath)
		if err != nil {
			t.Fatal(err)
		}
		f = follower.New(client, store, follower.Options{
			Roots:         []string{root},
			Platform:      "macos",
			ClientVersion: "0.1.0",
			Now:           func() time.Time { return clock },
			Jitter:        func(d time.Duration) time.Duration { return d },
			Log:           logger,
		})
	}
	start()

	for i, s := range sc.Steps {
		switch {
		case s.Write != nil:
			path := filepath.Join(root, filepath.FromSlash(s.Write.File))
			if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
				t.Fatal(err)
			}
			fh, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
			if err != nil {
				t.Fatal(err)
			}
			if _, err := fh.WriteString(s.Write.Text); err != nil {
				t.Fatal(err)
			}
			fh.Close()
		case s.Truncate != nil:
			if err := os.Truncate(filepath.Join(root, filepath.FromSlash(s.Truncate.File)), s.Truncate.Size); err != nil {
				t.Fatal(err)
			}
		case s.Delete != nil:
			if err := os.Remove(filepath.Join(root, filepath.FromSlash(s.Delete.File))); err != nil {
				t.Fatal(err)
			}
		case s.Tick > 0:
			for n := 0; n < s.Tick; n++ {
				f.Poll(context.Background())
				clock = clock.Add(f.PollInterval())
			}
		case s.Advance > 0:
			clock = clock.Add(time.Duration(s.Advance) * time.Second)
		case s.LoseState:
			os.Remove(statePath)
			start()
		case s.Restart:
			start()
		case s.Server != nil:
			server.mu.Lock()
			server.failNext += s.Server.Fail
			if s.Server.Status != 0 {
				server.status, server.times = s.Server.Status, s.Server.Times
			}
			server.mu.Unlock()
			if s.Server.Rewind != nil {
				server.rewind(s.Server.Rewind.File, s.Server.Rewind.AckedOffset)
			}
		default:
			t.Fatalf("step %d: unknown step %s", i, mustJSON(s))
		}
	}

	checkEveryFile(t, root, server)
	for file, want := range sc.Expect.Chunks {
		current := currentStream(t, root, server, file)
		if current == nil {
			t.Errorf("%s: no current stream", file)
		} else if got := len(current.Chunks); got != want {
			t.Errorf("%s: %d chunks, want %d", file, got, want)
		}
	}
	for file, want := range sc.Expect.Streams {
		if got := len(server.streamsFor(file)); got != want {
			t.Errorf("%s: %d streams registered, want %d", file, got, want)
		}
	}
	if sc.Expect.RequestsAtMost != nil && server.requests > *sc.Expect.RequestsAtMost {
		t.Errorf("%d requests, want at most %d", server.requests, *sc.Expect.RequestsAtMost)
	}
}

// checkEveryFile is the invariant every scenario must satisfy: the server has
// exactly the complete lines of every file, under the file's current fingerprint.
func checkEveryFile(t *testing.T, root string, server *fakeServer) {
	err := filepath.WalkDir(root, func(path string, entry os.DirEntry, err error) error {
		if err != nil || entry.IsDir() {
			return err
		}
		rel, _ := filepath.Rel(root, path)
		rel = filepath.ToSlash(rel)
		if ok, _ := filepath.Match("*.txt", entry.Name()); !ok {
			if n := len(server.streamsFor(rel)); n != 0 {
				t.Errorf("%s: %d streams for a file that should be ignored", rel, n)
			}
			return nil
		}
		content, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		cut := bytes.LastIndexByte(content, '\n')
		if cut < 0 {
			if n := len(server.streamsFor(rel)); n != 0 {
				t.Errorf("%s: %d streams for a file with no complete line", rel, n)
			}
			return nil
		}
		want := content[:cut+1]
		current := currentStream(t, root, server, rel)
		if current == nil {
			t.Errorf("%s: no stream with the file's fingerprint", rel)
			return nil
		}
		if got := current.bytes(); !bytes.Equal(got, want) {
			t.Errorf("%s: server has %d bytes, file has %d complete bytes", rel, len(got), len(want))
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
}

func currentStream(t *testing.T, root string, server *fakeServer, rel string) *fakeStream {
	content, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
	if err != nil {
		t.Fatal(err)
	}
	end := bytes.IndexByte(content, '\n')
	if end < 0 {
		return nil
	}
	sum := sha256.Sum256(content[:end+1])
	fingerprint := hex.EncodeToString(sum[:])
	var found *fakeStream
	for _, s := range server.streamsFor(rel) {
		if s.Fingerprint == fingerprint {
			if found != nil {
				t.Errorf("%s: two streams share the fingerprint", rel)
			}
			found = s
		}
	}
	return found
}

func mustJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}
