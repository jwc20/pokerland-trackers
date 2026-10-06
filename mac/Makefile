# Builds pokerland-tracker with the settings in the repo's .env.<ENV> file.
#
#   make build                     local build: ../.env.local, writes ./pokerland-tracker
#   make build ENV=production      what Homebrew and the release workflow build
#   make test
#
# VERSION defaults to the nearest git tag (e.g. 0.1.1-3-gabc1234), which servers
# accept; the release workflow and the formula pass the tag's version instead.
# GOOS/GOARCH in the environment cross-compile as usual.

ENV ?= local
OUT ?= pokerland-tracker

ENV_FILE := ../.env.$(ENV)
ifeq ($(wildcard $(ENV_FILE)),)
$(error $(ENV_FILE) not found; ENV must be local or production)
endif
include $(ENV_FILE)
ifeq ($(POKERLAND_API_BASE_URL),)
$(error $(ENV_FILE) does not set POKERLAND_API_BASE_URL)
endif

VERSION ?= $(shell git describe --tags --always --dirty 2>/dev/null | sed 's/^v//')
ifeq ($(VERSION),)
VERSION := 0.0.0-dev
endif

PKG := github.com/jwc20/pokerland-trackers/mac
LDFLAGS := -s -w -X main.version=$(VERSION) -X $(PKG)/internal/config.DefaultAPIBaseURL=$(POKERLAND_API_BASE_URL)

.PHONY: build test

build:
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o "$(OUT)" ./cmd/pokerland-tracker
	@echo "built $(OUT) $(VERSION) for $(ENV): $(POKERLAND_API_BASE_URL)"

test:
	go vet ./...
	go test ./...
