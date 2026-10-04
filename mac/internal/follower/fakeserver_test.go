package follower_test

import (
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"

	"github.com/jwc20/pokerland-trackers/mac/internal/api"
)

// fakeServer speaks the protocol over real HTTP, with fault injection for the scenarios.
type fakeServer struct {
	t        *testing.T
	mu       sync.Mutex
	config   api.Config
	streams  map[string]*fakeStream // by stream id
	requests int
	failNext int // reply 503 to this many requests
	status   int // reply this status to the next statusTimes requests
	times    int
	server   *httptest.Server
}

type fakeStream struct {
	ID          string
	Fingerprint string
	PathHint    string
	Acked       int64
	Chunks      []fakeChunk
}

type fakeChunk struct {
	Start, End int64
	Data       []byte
}

var (
	streamPath = regexp.MustCompile(`^/api/tracker/streams/([0-9a-f-]+)/$`)
	chunkPath  = regexp.MustCompile(`^/api/tracker/streams/([0-9a-f-]+)/chunks/(\d+)/$`)
)

func newFakeServer(t *testing.T, config api.Config) *fakeServer {
	f := &fakeServer{t: t, config: config, streams: map[string]*fakeStream{}}
	f.server = httptest.NewServer(http.HandlerFunc(f.handle))
	t.Cleanup(f.server.Close)
	return f
}

func (f *fakeServer) handle(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.requests++
	if !strings.HasPrefix(r.Header.Get("Authorization"), "Token ") {
		f.t.Errorf("missing client token on %s %s", r.Method, r.URL.Path)
	}
	if !strings.HasPrefix(r.Header.Get("User-Agent"), "pokerland-tracker/") {
		f.t.Errorf("missing tracker User-Agent on %s %s", r.Method, r.URL.Path)
	}
	if f.failNext > 0 {
		f.failNext--
		reply(w, 503, map[string]any{"detail": "injected failure"})
		return
	}
	if f.times > 0 {
		f.times--
		reply(w, f.status, map[string]any{"detail": "injected status"})
		return
	}

	switch {
	case r.URL.Path == "/api/tracker/config/":
		reply(w, 200, f.config)
	case r.URL.Path == "/api/tracker/me/":
		reply(w, 200, map[string]any{"username": "alice"})
	case streamPath.MatchString(r.URL.Path) && r.Method == http.MethodPut:
		f.register(w, r, streamPath.FindStringSubmatch(r.URL.Path)[1])
	case chunkPath.MatchString(r.URL.Path) && r.Method == http.MethodPut:
		m := chunkPath.FindStringSubmatch(r.URL.Path)
		start, _ := strconv.ParseInt(m[2], 10, 64)
		f.upload(w, r, m[1], start)
	default:
		reply(w, 404, map[string]any{"detail": "no such route"})
	}
}

func (f *fakeServer) register(w http.ResponseWriter, r *http.Request, id string) {
	var reg api.Registration
	if err := json.NewDecoder(r.Body).Decode(&reg); err != nil || len(reg.Fingerprint) != 64 || reg.Source == "" || reg.Platform == "" || reg.ClientVersion == "" {
		reply(w, 400, map[string]any{"detail": "bad registration"})
		return
	}
	stream, ok := f.streams[id]
	if !ok {
		stream = &fakeStream{ID: id, Fingerprint: reg.Fingerprint, PathHint: reg.PathHint}
		f.streams[id] = stream
		reply(w, 201, map[string]any{"stream_id": id, "acked_offset": 0})
		return
	}
	if stream.Fingerprint != reg.Fingerprint {
		reply(w, 409, map[string]any{"detail": "fingerprint mismatch"})
		return
	}
	stream.PathHint = reg.PathHint
	reply(w, 200, map[string]any{"stream_id": id, "acked_offset": stream.Acked})
}

func (f *fakeServer) upload(w http.ResponseWriter, r *http.Request, id string, start int64) {
	stream, ok := f.streams[id]
	if !ok {
		reply(w, 404, map[string]any{"detail": "unknown stream"})
		return
	}
	if r.Header.Get("Content-Type") != "application/gzip" {
		reply(w, 415, map[string]any{"detail": "expected application/gzip"})
		return
	}
	end, err := strconv.ParseInt(r.Header.Get("X-Chunk-End"), 10, 64)
	if err != nil || end <= start {
		reply(w, 400, map[string]any{"detail": "bad X-Chunk-End"})
		return
	}
	body, _ := io.ReadAll(r.Body)
	if int64(len(body)) > f.config.MaxChunkBytes {
		reply(w, 413, map[string]any{"detail": "too large"})
		return
	}
	zr, err := gzip.NewReader(bytes.NewReader(body))
	if err != nil {
		reply(w, 400, map[string]any{"detail": "not gzip"})
		return
	}
	data, err := io.ReadAll(zr)
	if err != nil {
		reply(w, 400, map[string]any{"detail": "bad gzip"})
		return
	}
	sum := sha256.Sum256(data)
	switch {
	case int64(len(data)) != end-start:
		reply(w, 400, map[string]any{"detail": "length mismatch"})
	case hex.EncodeToString(sum[:]) != strings.ToLower(r.Header.Get("X-Chunk-Sha256")):
		reply(w, 400, map[string]any{"detail": "hash mismatch"})
	case !bytes.HasSuffix(data, []byte("\n")):
		reply(w, 400, map[string]any{"detail": "no trailing newline"})
	case start != stream.Acked:
		for _, c := range stream.Chunks {
			if c.Start == start && c.End == end && bytes.Equal(c.Data, data) {
				reply(w, 200, map[string]any{"acked_offset": stream.Acked})
				return
			}
		}
		reply(w, 409, map[string]any{"detail": "expected another offset", "acked_offset": stream.Acked})
	default:
		stream.Chunks = append(stream.Chunks, fakeChunk{start, end, data})
		stream.Acked = end
		reply(w, 202, map[string]any{"acked_offset": end})
	}
}

// rewind makes the server forget everything past offset for a path's current stream.
func (f *fakeServer) rewind(pathHint string, offset int64) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var latest *fakeStream
	for _, s := range f.streams {
		if s.PathHint == pathHint {
			latest = s // scenarios only rewind files with one stream
		}
	}
	if latest == nil {
		f.t.Fatalf("rewind: no stream for %s", pathHint)
	}
	var kept []fakeChunk
	for _, c := range latest.Chunks {
		if c.End <= offset {
			kept = append(kept, c)
		}
	}
	latest.Chunks = kept
	latest.Acked = offset
}

func (f *fakeServer) streamsFor(pathHint string) []*fakeStream {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*fakeStream
	for _, s := range f.streams {
		if s.PathHint == pathHint {
			out = append(out, s)
		}
	}
	return out
}

func (s *fakeStream) bytes() []byte {
	var out []byte
	var expect int64
	for _, c := range s.Chunks {
		if c.Start != expect {
			panic("fake server stored a gap")
		}
		out = append(out, c.Data...)
		expect = c.End
	}
	return out
}

func reply(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}
