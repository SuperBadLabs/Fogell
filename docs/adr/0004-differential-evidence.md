# ADR 0004: Differential evidence before coverage

Status: Accepted

The differential harness is built before parser defects are fixed.

**Evidence.** Front-end acceptance is 93.9% of the corpus and 91.6% of the
Jenkins-ready set, with only 14 known rejections. Proven parity across all
prior engines is **5 files**. The gap between those two numbers *is* the
product, and no amount of parser work closes it.

**Harness contract.** For a given Jenkinsfile: run it on a pinned Jenkins
(image digest recorded) and on Fogell, on the same host, with the same inputs;
compare terminal result, ordered step sequence, and a canonical workspace hash.
A file is tier-1 only when that comparison passes and the receipt is sealed.

Untrusted-input rule: corpus and customer pipelines are third-party code.
Live execution is allowlisted by *executed surface*, on a no-egress network,
in a disposable workspace. Bounded inputs: source bytes, node count, and
nesting depth are capped before schema compilation.
