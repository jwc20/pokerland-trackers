package config_test

import (
	"testing"

	"github.com/jwc20/pokerland-trackers/mac/internal/config"
)

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
