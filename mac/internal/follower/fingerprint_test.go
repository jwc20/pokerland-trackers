package follower_test

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/jwc20/pokerland-trackers/mac/internal/follower"
)

func TestStreamIDMatchesTheServer(t *testing.T) {
	// The same vector as pokerland-api's tracker/tests_support.py (uuid5 of the fingerprint).
	fingerprint := "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
	if got := follower.StreamID(fingerprint); got != "67158b66-6466-5697-b139-fdc563e2573c" {
		t.Errorf("StreamID = %s", got)
	}
}

func TestFingerprintIsTheFirstLineWithBOM(t *testing.T) {
	path := filepath.Join(t.TempDir(), "a.txt")
	os.WriteFile(path, []byte("\xEF\xBB\xBFPokerStars Hand #1: x\nSeat 1: Alice\n"), 0o644)
	withBOM, _ := follower.Fingerprint(path)
	os.WriteFile(path, []byte("PokerStars Hand #1: x\nSeat 1: Alice\n"), 0o644)
	withoutBOM, _ := follower.Fingerprint(path)
	os.WriteFile(path, []byte("PokerStars Hand #1: x"), 0o644)
	partial, _ := follower.Fingerprint(path)

	if withBOM == withoutBOM {
		t.Error("the BOM must be part of the fingerprint")
	}
	if len(withBOM) != 64 {
		t.Errorf("fingerprint %q is not sha256 hex", withBOM)
	}
	if partial != "" {
		t.Error("a file without a newline has no fingerprint")
	}
}
