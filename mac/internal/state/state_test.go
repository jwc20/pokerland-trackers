package state_test

import (
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/jwc20/pokerland-trackers/mac/internal/state"
)

func TestSaveAndLoadRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nested", "state.json")
	store, err := state.Load(path)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 10, 3, 12, 0, 0, 0, time.UTC)
	store.Files["/a.txt"] = &state.File{StreamID: "id", Fingerprint: "fp", AckedOffset: 42, Registered: true, LastSeenAt: now}
	if err := store.Save(); err != nil {
		t.Fatal(err)
	}

	loaded, err := state.Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if got := loaded.Files["/a.txt"]; got == nil || got.AckedOffset != 42 || !got.Registered {
		t.Errorf("loaded %+v", got)
	}
	if _, err := os.Stat(path + ".tmp"); !os.IsNotExist(err) {
		t.Error("temp file left behind")
	}
}

func TestPruneForgetsOldFiles(t *testing.T) {
	store, _ := state.Load(filepath.Join(t.TempDir(), "state.json"))
	now := time.Date(2026, 10, 3, 12, 0, 0, 0, time.UTC)
	store.Files["/old.txt"] = &state.File{LastSeenAt: now.Add(-31 * 24 * time.Hour)}
	store.Files["/new.txt"] = &state.File{LastSeenAt: now.Add(-29 * 24 * time.Hour)}

	store.Prune(now)

	if _, ok := store.Files["/old.txt"]; ok {
		t.Error("old file kept")
	}
	if _, ok := store.Files["/new.txt"]; !ok {
		t.Error("recent file dropped")
	}
}

func TestCorruptStateIsAnError(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.json")
	os.WriteFile(path, []byte("{not json"), 0o600)

	if _, err := state.Load(path); err == nil {
		t.Error("expected an error so the user can delete the file knowingly")
	}
}
