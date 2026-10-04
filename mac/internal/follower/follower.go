// Package follower implements the tracker loop from protocol/PROTOCOL.md: find
// hand-history files, identify them, and upload their new bytes in order.
package follower

import (
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"math/rand/v2"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/jwc20/pokerland-trackers/mac/internal/api"
	"github.com/jwc20/pokerland-trackers/mac/internal/state"
)

const (
	source           = "pokerstars"
	minChunkBytes    = 4 * 1024
	initialBackoff   = time.Second
	maxBackoff       = 5 * time.Minute
	invalidTokenWait = 5 * time.Minute
	upgradeWait      = time.Hour
	configRefresh    = 6 * time.Hour
)

// Server is the part of api.Client the loop needs; tests use the real client
// against a fake HTTP server.
type Server interface {
	Config(ctx context.Context) (api.Config, error)
	RegisterStream(ctx context.Context, streamID string, reg api.Registration) (int64, error)
	UploadChunk(ctx context.Context, streamID string, start, end int64, sha256Hex string, gz []byte) (int64, error)
}

type Options struct {
	Roots         []string
	Include       string // glob on the file name, e.g. "*.txt"
	Platform      string
	ClientVersion string
	Now           func() time.Time
	// Jitter stretches a back-off delay; nil means random up to +50%.
	Jitter func(time.Duration) time.Duration
	Log    *slog.Logger
	// Notify shows the user a message they must act on (bad token, update needed).
	Notify func(title, message string)
}

// Status is what the daemon reports to `pokerland-tracker status`.
type Status struct {
	LastPollAt    time.Time `json:"last_poll_at"`
	LastUploadAt  time.Time `json:"last_upload_at"`
	LastError     string    `json:"last_error,omitempty"`
	PausedReason  string    `json:"paused_reason,omitempty"`
	PausedUntil   time.Time `json:"paused_until,omitempty"`
	FilesTracked  int       `json:"files_tracked"`
	BytesUploaded int64     `json:"bytes_uploaded"`
}

type Follower struct {
	opts   Options
	server Server
	store  *state.Store
	config api.Config

	configFetchedAt time.Time
	chunkLimit      int64 // bytes read per chunk; halved after a 413
	backoff         time.Duration
	retryAt         time.Time
	pausedUntil     time.Time
	pausedReason    string
	status          Status
}

func New(server Server, store *state.Store, opts Options) *Follower {
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.Jitter == nil {
		opts.Jitter = func(d time.Duration) time.Duration { return d + time.Duration(rand.Int64N(int64(d/2)+1)) }
	}
	if opts.Log == nil {
		opts.Log = slog.Default()
	}
	if opts.Notify == nil {
		opts.Notify = func(string, string) {}
	}
	if opts.Include == "" {
		opts.Include = "*.txt"
	}
	return &Follower{opts: opts, server: server, store: store, config: api.DefaultConfig, chunkLimit: api.DefaultConfig.MaxReadBytes}
}

func (f *Follower) Config() api.Config { return f.config }
func (f *Follower) Status() Status     { return f.status }

// PollInterval is how long Run waits between polls.
func (f *Follower) PollInterval() time.Duration {
	return time.Duration(f.config.PollIntervalSeconds) * time.Second
}

// Run polls until ctx is cancelled.
func (f *Follower) Run(ctx context.Context) {
	for {
		f.Poll(ctx)
		select {
		case <-ctx.Done():
			return
		case <-time.After(f.PollInterval()):
		}
	}
}

// Poll is one pass over every file. It never blocks on the network for longer
// than one request chain; failures are retried on later polls.
func (f *Follower) Poll(ctx context.Context) {
	now := f.opts.Now()
	f.status.LastPollAt = now
	f.refreshConfig(ctx, now)

	for _, path := range f.scan() {
		if ctx.Err() != nil {
			return
		}
		if err := f.pollFile(ctx, path); err != nil {
			f.opts.Log.Warn("file skipped", "path", path, "error", err)
		}
	}
	f.store.Prune(now)
	f.status.FilesTracked = len(f.store.Files)
	if err := f.store.Save(); err != nil {
		f.opts.Log.Error("saving state failed", "error", err)
	}
}

func (f *Follower) refreshConfig(ctx context.Context, now time.Time) {
	if !f.configFetchedAt.IsZero() && now.Sub(f.configFetchedAt) < configRefresh {
		return
	}
	if !f.canSend(now) {
		return
	}
	config, err := f.server.Config(ctx)
	if err != nil {
		f.handleError(err, now)
		return
	}
	f.configFetchedAt = now
	f.config = config
	if f.chunkLimit > config.MaxReadBytes || f.chunkLimit == api.DefaultConfig.MaxReadBytes {
		f.chunkLimit = config.MaxReadBytes
	}
}

// scan lists every file under the roots whose name matches Include.
func (f *Follower) scan() []string {
	var paths []string
	for _, root := range f.opts.Roots {
		_ = filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
			if err != nil || entry.IsDir() {
				return nil // unreadable entries are skipped, not fatal
			}
			if ok, _ := filepath.Match(f.opts.Include, entry.Name()); ok {
				paths = append(paths, path)
			}
			return nil
		})
	}
	return paths
}

func (f *Follower) pollFile(ctx context.Context, path string) error {
	now := f.opts.Now()
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	size := info.Size()
	file := f.store.Files[path]
	if file == nil {
		file = &state.File{}
	}
	file.LastSeenAt = now

	// Identify: a new file, a shrunk file, or a changed first line.
	if file.StreamID == "" || size < file.AckedOffset || size != file.LastSize {
		fingerprint, err := Fingerprint(path)
		if err != nil {
			return err
		}
		if fingerprint == "" {
			return nil // no complete first line yet
		}
		if fingerprint != file.Fingerprint {
			file = &state.File{Fingerprint: fingerprint, StreamID: StreamID(fingerprint), LastSeenAt: now}
		} else if size < file.AckedOffset {
			file.Registered = false // rewritten identically; ask the server where it stands
		}
		file.LastSize = size
		f.store.Files[path] = file
	}

	if !f.canSend(now) {
		return nil
	}
	if !file.Registered {
		acked, err := f.server.RegisterStream(ctx, file.StreamID, api.Registration{
			Source:        source,
			Platform:      f.opts.Platform,
			ClientVersion: f.opts.ClientVersion,
			PathHint:      f.pathHint(path),
			Fingerprint:   file.Fingerprint,
		})
		if err != nil {
			f.handleError(err, now)
			return nil
		}
		f.recovered()
		file.Registered = true
		file.AckedOffset = acked
		if err := f.store.Save(); err != nil {
			return err
		}
	}

	pending := size - file.AckedOffset
	due := file.LastUploadAt.IsZero() || pending >= f.config.FlushBytes ||
		now.Sub(file.LastUploadAt) >= time.Duration(f.config.FlushIntervalSeconds)*time.Second
	if pending <= 0 || !due {
		return nil
	}
	return f.uploadBacklog(ctx, path, file, size)
}

// uploadBacklog sends every complete line past the acked offset, chunk by chunk.
func (f *Follower) uploadBacklog(ctx context.Context, path string, file *state.File, size int64) error {
	for file.AckedOffset < size && f.canSend(f.opts.Now()) {
		data, err := readChunk(path, file.AckedOffset, min(f.chunkLimit, f.config.MaxReadBytes))
		if err != nil {
			return err
		}
		if len(data) == 0 {
			return nil // only a partial line remains
		}
		gz, err := compress(data)
		if err != nil {
			return err
		}
		if int64(len(gz)) > f.config.MaxChunkBytes {
			if !f.shrinkChunks() {
				return fmt.Errorf("a %d-byte chunk still compresses past max_chunk_bytes", len(data))
			}
			continue
		}
		sum := sha256.Sum256(data)
		start, end := file.AckedOffset, file.AckedOffset+int64(len(data))
		acked, err := f.server.UploadChunk(ctx, file.StreamID, start, end, hex.EncodeToString(sum[:]), gz)
		now := f.opts.Now()
		if err != nil {
			if !f.handleUploadError(err, file, now) {
				return nil
			}
			continue
		}
		f.recovered()
		file.AckedOffset = acked
		file.LastUploadAt = now
		f.status.LastUploadAt = now
		f.status.BytesUploaded += int64(len(data))
		if err := f.store.Save(); err != nil {
			return err
		}
		f.opts.Log.Info("uploaded", "path", filepath.Base(path), "start", start, "end", end, "gzip_bytes", len(gz))
	}
	return nil
}

// handleUploadError reports whether the loop should try the file again right away.
func (f *Follower) handleUploadError(err error, file *state.File, now time.Time) bool {
	var status *api.StatusError
	if errors.As(err, &status) {
		switch {
		case status.Status == http.StatusConflict && status.AckedOffset != nil:
			if *status.AckedOffset == file.AckedOffset {
				// The server has different bytes at this offset: retrying cannot help.
				f.opts.Log.Error("server holds different bytes for this file", "offset", file.AckedOffset, "detail", status.Detail)
				f.status.LastError = status.Error()
				return false
			}
			f.opts.Log.Info("server is at another offset; following it", "server_offset", *status.AckedOffset, "ours", file.AckedOffset)
			file.AckedOffset = *status.AckedOffset
			return true
		case status.Status == http.StatusNotFound:
			file.Registered = false // re-register on the next poll
			return false
		case status.Status == http.StatusRequestEntityTooLarge:
			return f.shrinkChunks()
		case status.Status == http.StatusBadRequest:
			f.opts.Log.Warn("chunk rejected; will re-read the file", "detail", status.Detail)
			return false
		}
	}
	f.handleError(err, now)
	return false
}

// handleError applies the PROTOCOL.md table for replies that affect every request.
func (f *Follower) handleError(err error, now time.Time) {
	f.status.LastError = err.Error()
	var status *api.StatusError
	if errors.As(err, &status) {
		switch status.Status {
		case http.StatusUnauthorized:
			f.pause(now, invalidTokenWait, "client token rejected")
			f.opts.Notify("Pokerland tracker", "Your client token was rejected. Run `pokerland-tracker login` with the token from your Settings page.")
			return
		case http.StatusUpgradeRequired:
			f.pause(now, upgradeWait, "update required")
			f.opts.Notify("Pokerland tracker", "This tracker version is no longer supported. Run `brew upgrade pokerland-tracker`.")
			return
		}
	}
	if api.Retryable(err) {
		if f.backoff == 0 {
			f.backoff = initialBackoff
		} else {
			f.backoff = min(f.backoff*2, maxBackoff)
		}
		f.retryAt = now.Add(f.opts.Jitter(f.backoff))
		f.opts.Log.Warn("request failed; backing off", "error", err, "retry_in", f.backoff)
		return
	}
	f.opts.Log.Warn("request failed", "error", err)
}

func (f *Follower) recovered() {
	f.backoff = 0
	f.retryAt = time.Time{}
	f.pausedUntil = time.Time{}
	f.pausedReason = ""
	f.status.LastError = ""
	f.status.PausedReason = ""
	f.status.PausedUntil = time.Time{}
}

func (f *Follower) pause(now time.Time, d time.Duration, reason string) {
	f.pausedUntil = now.Add(d)
	f.pausedReason = reason
	f.status.PausedReason = reason
	f.status.PausedUntil = f.pausedUntil
	f.opts.Log.Warn("uploads paused", "reason", reason, "until", f.pausedUntil)
}

func (f *Follower) canSend(now time.Time) bool {
	return !now.Before(f.pausedUntil) && !now.Before(f.retryAt)
}

// shrinkChunks halves the chunk size and reports whether it could.
func (f *Follower) shrinkChunks() bool {
	if f.chunkLimit <= minChunkBytes {
		return false
	}
	f.chunkLimit = max(f.chunkLimit/2, minChunkBytes)
	f.opts.Log.Info("chunk too large; halving", "bytes", f.chunkLimit)
	return true
}

// pathHint is the path relative to its root, with forward slashes.
func (f *Follower) pathHint(path string) string {
	for _, root := range f.opts.Roots {
		if rel, err := filepath.Rel(root, path); err == nil && !strings.HasPrefix(rel, "..") {
			return filepath.ToSlash(rel)
		}
	}
	return filepath.Base(path)
}

// readChunk returns up to limit bytes from offset, cut at the last newline.
// The file is opened for this read only and never held between polls.
func readChunk(path string, offset, limit int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	buf := make([]byte, limit)
	n, err := f.ReadAt(buf, offset)
	if err != nil && err != io.EOF {
		return nil, err
	}
	cut := bytes.LastIndexByte(buf[:n], '\n')
	if cut < 0 {
		return nil, nil
	}
	return buf[:cut+1], nil
}

func compress(data []byte) ([]byte, error) {
	var out bytes.Buffer
	w, err := gzip.NewWriterLevel(&out, gzip.BestCompression)
	if err != nil {
		return nil, err
	}
	if _, err := w.Write(data); err != nil {
		return nil, err
	}
	if err := w.Close(); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}

// UserAgent builds the header the server reads the version from.
func UserAgent(version, platform, arch string) string {
	return fmt.Sprintf("pokerland-tracker/%s (%s; %s)", version, platform, arch)
}
