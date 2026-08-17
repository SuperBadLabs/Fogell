/*
 MODIFIED PIN — NOT the upstream Jenkinsfile. See bench/DEVIATIONS.md.
 Upstream: jenkinsci/git-plugin @ git-5.10.1, sha256:60130b33d80d962e
 Deviation: the [platform: 'windows', jdk: 21] configuration is REMOVED.
 Reason: this fleet has no Windows agent and does not support the Windows lane.
 Everything else is byte-identical to upstream.
*/
buildPlugin(
  forkCount: '1C', // Run a JVM per core in tests
  // we use Docker for containerized tests
  useContainerAgent: false,
  configurations: [
    [platform: 'linux', jdk: 25],
])
