# Template for the formula in the tap repo (github.com/jwc20/homebrew-tap,
# Formula/pokerland-tracker.rb). The release workflow fills in the version and
# the sha256 of the tag's source tarball and pushes it there.
#
# Building from source means no Apple notarization is needed, and a formula
# (not a cask) is what `brew services` can manage.
class PokerlandTracker < Formula
  desc "Uploads PokerStars hand histories to Pokerland"
  homepage "https://github.com/jwc20/pokerland-trackers"
  url "https://github.com/jwc20/pokerland-trackers/archive/refs/tags/v{{VERSION}}.tar.gz"
  sha256 "{{SHA256}}"
  license "MIT"

  depends_on "go" => :build

  def install
    cd "mac" do
      system "go", "build", *std_go_args(ldflags: "-s -w -X main.version=#{version}"), "./cmd/pokerland-tracker"
    end
  end

  def caveats
    <<~EOS
      Save your client token (from the Settings page of the web app), then start the service:
        pokerland-tracker login
        brew services start pokerland-tracker
    EOS
  end

  service do
    run [opt_bin/"pokerland-tracker", "run"]
    keep_alive true
    log_path var/"log/pokerland-tracker.log"
    error_log_path var/"log/pokerland-tracker.log"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/pokerland-tracker version")
  end
end
