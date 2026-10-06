package config_test

import (
	"bufio"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/jwc20/pokerland-trackers/mac/internal/config"
)

// A plain `go build` (no Makefile) must still talk to production.
func TestFallbackMatchesEnvProduction(t *testing.T) {
	f, err := os.Open(filepath.Join("..", "..", "..", ".env.production"))
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		if value, ok := strings.CutPrefix(scanner.Text(), "POKERLAND_API_BASE_URL="); ok {
			if value != config.DefaultAPIBaseURL {
				t.Errorf("DefaultAPIBaseURL is %q, .env.production says %q", config.DefaultAPIBaseURL, value)
			}
			return
		}
	}
	t.Fatal(".env.production has no POKERLAND_API_BASE_URL")
}

func TestLoadDefaultsToTheBuiltInURL(t *testing.T) {
	cfg, err := config.Load(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	if cfg.APIBaseURL != config.DefaultAPIBaseURL || cfg.Token != "" {
		t.Errorf("got %+v", cfg)
	}
}

func TestSavedURLWins(t *testing.T) {
	dir := t.TempDir()
	if err := (&config.Config{APIBaseURL: "http://elsewhere:9000", Token: "t"}).Save(dir); err != nil {
		t.Fatal(err)
	}
	cfg, err := config.Load(dir)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.APIBaseURL != "http://elsewhere:9000" {
		t.Errorf("APIBaseURL = %q", cfg.APIBaseURL)
	}
}
