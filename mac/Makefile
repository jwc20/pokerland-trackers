# Builds pokerland-tracker for a server environment.
#
#   make build                     local build (http://127.0.0.1:8000), writes ./pokerland-tracker
#   make build ENV=production      what Homebrew and the release workflow build
#
# The production URL is the default in internal/config/config.go. An optional,
# untracked ../.env.<ENV> file, or POKERLAND_API_BASE_URL in the environment,
# overrides the URL for that ENV.
#   make test
#
# VERSION defaults to the nearest git tag (e.g. 0.1.1-3-gabc1234), which servers
# accept; the release workflow and the formula pass the tag's version instead.
# GOOS/GOARCH in the environment cross-compile as usual.

ENV ?= local
OUT ?= pokerland-tracker

ifeq ($(filter $(ENV),local production),)
$(error ENV must be local or production)
endif
-include ../.env.$(ENV)
ifeq ($(ENV),local)
POKERLAND_API_BASE_URL ?= http://127.0.0.1:8000
endif

VERSION ?= $(shell git describe --tags --always --dirty 2>/dev/null | sed 's/^v//')
ifeq ($(VERSION),)
VERSION := 0.0.0-dev
endif

PKG := github.com/jwc20/pokerland-trackers/mac
LDFLAGS := -s -w -X main.version=$(VERSION)
ifneq ($(POKERLAND_API_BASE_URL),)
LDFLAGS += -X $(PKG)/internal/config.DefaultAPIBaseURL=$(POKERLAND_API_BASE_URL)
endif

.PHONY: build test

build:
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o "$(OUT)" ./cmd/pokerland-tracker
	@echo "built $(OUT) $(VERSION) for $(ENV): $(or $(POKERLAND_API_BASE_URL),built-in production URL)"

test:
	go vet ./...
	go test ./...
