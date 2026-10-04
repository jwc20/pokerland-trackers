// Package state persists what the server has acknowledged for each file, so a
// restarted tracker continues where it stopped. See "Local state" in
// protocol/PROTOCOL.md.
package state

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"time"
)

const Version = 1

// Forget files not seen for this long: they were deleted or moved.
const retention = 30 * 24 * time.Hour

type File struct {
	StreamID     string    `json:"stream_id"`
	Fingerprint  string    `json:"fingerprint"`
	AckedOffset  int64     `json:"acked_offset"`
	Registered   bool      `json:"registered"`
	LastUploadAt time.Time `json:"last_upload_at"`
	LastSeenAt   time.Time `json:"last_seen_at"`
	// The file's size at the last poll; a fingerprint is only rechecked when it changes.
	LastSize int64 `json:"last_size"`
}

type Store struct {
	path  string
	Files map[string]*File // keyed by absolute path
}

func Load(path string) (*Store, error) {
	store := &Store{path: path, Files: map[string]*File{}}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return store, nil
	}
	if err != nil {
		return nil, err
	}
	var saved struct {
		Version int              `json:"version"`
		Files   map[string]*File `json:"files"`
	}
	if err := json.Unmarshal(data, &saved); err != nil {
		return nil, err
	}
	if saved.Version == Version && saved.Files != nil {
		store.Files = saved.Files
	}
	return store, nil
}

// Save writes the state atomically: a crash mid-write cannot leave a corrupt file.
func (s *Store) Save() error {
	data, err := json.MarshalIndent(struct {
		Version int              `json:"version"`
		Files   map[string]*File `json:"files"`
	}{Version, s.Files}, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(s.path), 0o700); err != nil {
		return err
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, s.path)
}

// Prune drops files not seen since before the retention window.
func (s *Store) Prune(now time.Time) {
	for path, file := range s.Files {
		if now.Sub(file.LastSeenAt) > retention {
			delete(s.Files, path)
		}
	}
}
