# Bench deviations — every place a pinned pipeline differs from upstream

A bench that silently edits its inputs measures its own edits. Every deviation is
listed here with what changed, why, and what it costs the resulting number. A
project with NO entry here runs upstream's Jenkinsfile byte-for-byte.

## jenkinsci/git-plugin @ git-5.10.1

- **Upstream Jenkinsfile sha256**: `60130b33d80d962e…` (first 16 hex chars)
- **Modified copy**: `bench/pipelines/jenkinsci_git-plugin.Jenkinsfile`
- **Change**: removed `[platform: 'windows', jdk: 21]` from `configurations`.
  One line. Nothing else differs.
- **Why**: this fleet has no Windows agent, and the standing position is that the
  Windows lane is unsupported and said so plainly rather than faked.
- **WHAT THIS COSTS THE NUMBER**: the build exercises ONE of the two platforms
  upstream tests. A green here is evidence about `linux/jdk25` and about nothing
  else. It is NOT evidence that git-plugin builds, and no result from this pin may
  be reported as "git-plugin builds under Fogell" without the platform named.
- **What it does NOT cost**: the Jenkins-versus-Fogell comparison is unaffected.
  Both engines receive this same modified text, so the deviation cancels in the
  differential even though it bounds the absolute claim.

### Applies to the other two plugin pins when they are enabled

`jenkinsci/configuration-as-code-plugin` and `jenkinsci/workflow-cps-plugin` carry the
same `windows/jdk21` axis and will need the same deviation. They are NOT modified yet —
this file gets an entry when they are, not before.
