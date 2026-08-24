#!/usr/bin/env bb
;; FG-090/091/092. Generates the compatibility scorecard, the machine-readable
;; ledger, and KNOWN-LIMITATIONS.md — from evidence on disk, never by hand.
;;
;; ADR 0001 fixes three tiers and forbids collapsing them into one number, with the
;; reason recorded: a prior engine had 146 non-empty IRs and 5 files of proven
;; parity, and a single percentage would have implied 64%.
;;
;; THE SAME TRAP IS LIVE IN THIS REPO. Nearly every differential receipt is for a
;; hand-written case in `differential/cases`, not for a corpus Jenkinsfile — the
;; name overlap between the two populations is the handful of corpus receipts the
;; ledger's tier-1 rows name (zero until 2026-08-17, when FG-200 landed the first).
;; Printing a case count anywhere near "228 files" invites dividing one by the
;; other, and the corpus figure for proven parity is the tier-1 count alone.
;; (This header once carried the live counts — "118 receipts", "0 of 228" — and
;; both went stale in place, caught by the FG-201-cycle verifier: a .bb comment is
;; outside every audit's reach, so it must not carry a number an audit cannot see.)
;; The two populations are
;; therefore reported in SEPARATE SECTIONS, each with its own denominator stated on
;; the same line as its count, and no ratio is ever computed.
;;
;; Parse only. Corpus files are untrusted third-party CI code: this reads and parses
;; them and never executes one.
;;
;;   usage: scripts/generate-scorecard.bb [--check]
;;          --check regenerates IN MEMORY and fails if the committed artifacts
;;          differ from what the current evidence produces. It writes nothing.
;;
;;          IT DOES NOT PROTECT EVERY HOST. `build-and-test.sh` skips this check
;;          when `FOGELL_CORPUS` is absent, and CI excludes corpus work by design,
;;          so a stale artifact passes on GitHub. Drift is caught on luigi/HeMan
;;          only. This comment previously said a stale scorecard "cannot survive a
;;          gate run" — the THIRD copy of that claim, after the board row and the
;;          build script, and I corrected the other two and left this one.

(require '[babashka.fs :as fs] '[babashka.process :as p] '[clojure.string :as str])

(let [check? (some #{"--check"} *command-line-args*)
      root (str (fs/parent (fs/parent (fs/absolutize *file*))))
      corpus (or (System/getenv "FOGELL_CORPUS")
                 "/sn8100/work/exchange/crucible-gate/corpus")
      expected-core (or (System/getenv "FOGELL_JENKINS_CORE") "2.568.1")

      ;; THE CORPUS GATE RUNS FIRST, as its own header demands. A drifted corpus
      ;; invalidates every count below it, so a scorecard generated over one is
      ;; worse than none — it looks authoritative and is not.
      gate (p/shell {:dir root :out :string :err :string :continue true}
                    "scripts/verify-corpus.sh")
      _ (when-not (zero? (:exit gate))
          (println "FAIL: corpus gate refused; no scorecard generated")
          (println (str/trim (str (:out gate) (:err gate))))
          (System/exit 1))

      score (p/shell {:dir root :out :string :err :string :continue true}
                     "dotnet" "run" "--project" "tools/Fogell.Corpus.Score"
                     "-c" "Release" "--no-build" "--" corpus)
      _ (when-not (zero? (:exit score))
          (println "FAIL: corpus scorer did not run")
          (println (str/trim (str (:err score))))
          (System/exit 1))

      rows (->> (str/split-lines (:out score))
                (drop 1)
                (remove str/blank?)
                (map #(str/split % #"\t" -1))
                (map (fn [[file verdict code stages steps detail]]
                       {:file file :verdict verdict :code code
                        :stages stages :steps steps :detail detail})))

      ;; A receipt proves parity for the file it NAMES. Receipts are keyed by case
      ;; name; a corpus file earns tier 1 only if a receipt carries its exact name.
      ;; THE EXPECTED RECEIPT SET, DERIVED FROM THE CASES THEMSELVES. A case containing
      ;; `//// NEXT BUILD ////` separators is a SEQUENCE and emits `<case>.b1` … `.bN`
      ;; for N = separators + 1; every other case emits `<case>`. A receipt counts only
      ;; if it is a name the current cases would actually produce.
      ;;
      ;; Two weaker filters preceded this. Matching receipt name to case name called all
      ;; 12 per-build receipts orphans and cut the headline from 118 to 106 — a DEFLATED
      ;; number I would have reported as confirming a review finding. Stripping the `.bN`
      ;; suffix fixed that and still accepted a STALE BUILD NUMBER: shorten a four-build
      ;; sequence to two and `.b3`/`.b4` survive, map to the live case, and keep counting.
      ;; Deriving the set closes both, because it asks what the cases PRODUCE rather than
      ;; what a receipt name RESEMBLES.
      ;; MIRRORS THE WRITER, deliberately. `Compare.fs` builds the receipt name as
      ;; `r.File.Replace("/", "_").Replace(".Jenkinsfile", "")` — a GLOBAL replace — so
      ;; `foo.Jenkinsfile.Jenkinsfile` becomes `foo.receipt.txt`. I "fixed" this to an
      ;; ANCHORED regex last round, which made the reader disagree with the writer for
      ;; exactly that name: a correction in the wrong direction, since the reader's job
      ;; is to predict what the writer produced, not to improve on it.
      ;;
      ;; Both are odd for a name containing `.Jenkinsfile` twice; FG-163 carries fixing
      ;; the writer. Until then they agree, which is the property that matters here.
      stem-of (fn [n] (-> n (str/replace "/" "_") (str/replace ".Jenkinsfile" "")))

      ;; Carry the originating case beside every exact name it can emit. Staleness
      ;; is a relation between those two physical files; reversing a receipt name
      ;; cannot recover it unambiguously because a singleton case may itself be
      ;; named `foo.b1.Jenkinsfile`.
      expected-mappings
      (->> (fs/glob (fs/file root "differential/cases") "*.Jenkinsfile")
           (mapcat (fn [f]
                     (let [stem (stem-of (fs/file-name f))
                           builds (inc (count (re-seq #"(?m)^//// NEXT BUILD ////\s*$"
                                                      (slurp (fs/file f)))))]
                       (if (= 1 builds)
                         [{:receipt stem :case f}]
                         (map (fn [build] {:receipt (str stem ".b" build) :case f})
                              (range 1 (inc builds))))))))

      ;; A COLLISION IS AN ERROR, NOT A DEDUPLICATION. `foo.Jenkinsfile` as a sequence
      ;; synthesises `foo.b1`, and a separate case `foo.b1.Jenkinsfile` expects the same
      ;; name; `set` silently collapsed two expected builds into one and quietly shrank
      ;; the denominator this counting path was just fixed to protect.
      _ (let [dups (->> expected-mappings
                         (map :receipt)
                         frequencies
                         (filter #(> (val %) 1))
                         (map key)
                         sort)]
          (when (seq dups)
            (println "FAIL: two cases expect the same receipt name:" (str/join ", " dups))
            (System/exit 1)))

      ;; Construct the lookup only AFTER proving names unique. Building a map first
      ;; would let last-writer-wins erase the collision this check exists to refuse.
      expected-case-by-receipt
      (into {} (map (juxt :receipt :case) expected-mappings))

      expected-receipts (set (keys expected-case-by-receipt))

      ;; A RECEIPT NAMING A CORPUS FILE IS TIER-1 EVIDENCE and must survive this filter.
      ;; The orphan filter I added discarded it before `tier-of` looked, so a corpus file
      ;; with its own receipt stayed `admitted` forever — the filter closed the tier-1
      ;; path that FG-090 exists to open. Corpus stems join the keep-set; the case-suite
      ;; section still counts only case receipts, below.
      corpus-stems (set (map #(stem-of (:file %)) rows))

      receipts (->> (fs/glob (fs/file root "differential/receipts") "*.receipt.txt")
                    (filter (fn [f]
                              (let [n (str/replace (fs/file-name f) #"\.receipt\.txt$" "")]
                                (or (contains? expected-receipts n)
                                    (contains? corpus-stems n)))))
                    (map (fn [p]
                           (let [n (str/replace (fs/file-name p) #"\.receipt\.txt$" "")
                                 body (slurp (fs/file p))]
                             ;; THE TIER-1 VERDICT FIELD, matched to its end. `PROVEN-PARTIAL`
                             ;; also starts with "PROVEN" and the comparator emits it to
                             ;; say the workspace could NOT be compared — explicitly not
                             ;; tier 1. A prefix match would have counted it as proven and,
                             ;; if its name matched a corpus file, promoted that file to
                             ;; tier 1: a false tier-1 produced by the very generator built
                             ;; to prevent false tiers. There are 0 such receipts today, so
                             ;; the bug was invisible and correct by accident.
                             ;; The tier phrase must END the field: end-of-line, or the
                             ;; em-dash detail the renderer writes. The previous regex was
                             ;; still a PREFIX — `PROVEN (tier 1) BUT ACTUALLY NOT` would
                             ;; have matched it — while the comment above claimed it was
                             ;; exact. An overclaim inside the fix for an overclaim.
                             ;; THE CORE MUST MATCH. ADR 0001's tier 1 is parity against a
                             ;; PINNED Jenkins version; a receipt records the core it ran
                             ;; against. Counting the verdict without reading that field
                             ;; let a suite total — and any corpus promotion — mix evidence
                             ;; from different Jenkins versions, which is not the claim
                             ;; tier 1 makes.
                             [n (let [core-line (second (re-find #"(?m)^jenkins-core:\s*(\S+)" body))
                                      tier1? (re-find #"(?m)^VERDICT: PROVEN \(tier 1\)(?:\s+—[^\n]*)?$" body)]
                                  (cond
                                    (not tier1?) :other
                                    (not= core-line expected-core) :wrong-core
                                    :else :proven))])))
                    (into {}))

      ;; REJECTION WINS OVER AN OLD RECEIPT. A receipt proves what the engine did when
      ;; it ran; if the CURRENT engine cannot parse the file, "proven compatible" is a
      ;; claim about a binary that no longer exists. The receipt branch used to come
      ;; first, so a parser regression on a receipted file would have published it as
      ;; tier 1 — wrong by construction, the same correct-by-accident shape as the
      ;; PROVEN-PARTIAL match. (This line said "unreachable today (no corpus file has
      ;; a receipt)" until FG-200 made the branch reachable, and its receipt rides it.)
      ;;
      ;; :admitted IS NOT ADR TIER 2. The ADR defines tier 2 as parses AND EXECUTES;
      ;; this scorer only parses, because corpus files are untrusted and are never run.
      ;; Calling them tier 2 asserted an execution result nobody measured. They are
      ;; reported under their own name, and ADR tier 2 is reported as NOT ASSESSED.
      tier-of (fn [{:keys [file verdict]}]
                (let [stem (str/replace file ".Jenkinsfile" "")]
                  (cond
                    (contains? #{"err" "scripted-err"} verdict) 3
                    (= :proven (get receipts stem)) 1
                    :else :admitted)))

      ledger (->> rows
                  (map (fn [r]
                         (let [t (tier-of r)]
                           (assoc r :tier t
                                  :evidence (case t
                                              1 (str "receipt:" (str/replace (:file r) ".Jenkinsfile" ""))
                                              3 (str (:code r) " " (:detail r))
                                              :admitted "parsed; execution NOT attempted (untrusted corpus) — not ADR tier 2")))))
                  (sort-by :file))

      by-tier (group-by :tier ledger)
      t1 (count (get by-tier 1 []))
      t2 (count (get by-tier :admitted []))
      t3 (count (get by-tier 3 []))
      total (count ledger)

      by-code (->> (get by-tier 3 [])
                   (group-by :code)
                   (map (fn [[c xs]] [c (count xs)]))
                   (sort-by (comp - second)))

      ;; COMPUTED, never asserted. The scorecard used to state "the name overlap is
      ;; currently zero" as a literal, in a document whose own classifier promotes a
      ;; corpus file to tier 1 the moment a receipt matches its stem. The first
      ;; corpus-backed receipt would have produced a table reading tier1=1 above prose
      ;; reading overlap=zero, and `--check` would have regenerated and accepted it,
      ;; because a constant always matches itself. Fourth correct-by-accident claim on
      ;; this branch, and the first inside a generated artifact.

      ;; THE DENOMINATOR IS WHAT THE CASES EXPECT, not what happens to be on disk. A
      ;; case with no receipt — newly added, or its receipt deleted — was absent from
      ;; `receipts` entirely, so it left BOTH sides of the fraction and the suite still
      ;; read "118 of 118 proven" with a case unproven. A metric that shrinks its own
      ;; denominator can never report a shortfall.
      case-expected (count expected-receipts)
      ;; A RECEIPT OLDER THAN ITS CASE PROVES A FILE THAT NO LONGER EXISTS. Editing a
      ;; case without renaming it leaves the expected receipt name unchanged, so the old
      ;; `:proven` receipt kept counting and `--check` published the suite as fully
      ;; proven with the changed case never re-run. The seal binds the file NAME, not its
      ;; contents, so nothing else catches this.
      ;;
      ;; FG-161 CLOSED THE ENFORCEMENT GAP, and this generator is no longer the only
      ;; thing standing between an edited receipt and a published proven count. The gate
      ;; now runs `--verify-seals`, which recomputes each seal from the receipt's own
      ;; content — including the VERDICT LINE, which this script reads to classify
      ;; `:proven` and which the seal did not bind until FG-161.
      ;;
      ;; WHAT THIS SCRIPT STILL DOES NOT DO: it does not verify seals itself. That check
      ;; lives where the hash is computed, because reimplementing it in babashka is how
      ;; the three copies of the timestamp rule came to disagree. It classifies from the
      ;; verdict line, which is now sealed, so a doctored line no longer passes the gate
      ;; as a whole — but it passes THIS script, and the two run together or not at all.
      ;;
      ;; The mtime warning survives as the FRESHNESS half: a receipt whose case changed
      ;; on disk still seals validly, because the seal binds the case digest recorded
      ;; when it ran. Verification proves the receipt is intact; mtime is what notices
      ;; the case moved underneath it.
      ;; MTIME IS ENVIRONMENT STATE AND NEVER ENTERS THE DOCUMENT. A fresh checkout
      ;; gives arbitrary mtimes, so interpolating this into a byte-compared artifact
      ;; would make `--check` fail on a clean clone and publish spurious STALE text —
      ;; the artifact must be a pure function of CONTENT. It is printed as a runtime
      ;; warning instead, which is where an environment-dependent signal belongs.
      stale-receipts
      (->> (fs/glob (fs/file root "differential/receipts") "*.receipt.txt")
           (keep (fn [f]
                   (let [n (str/replace (fs/file-name f) #"\.receipt\.txt$" "")]
                     (when-let [case-file (get expected-case-by-receipt n)]
                       (when (pos? (compare (fs/last-modified-time (fs/file case-file))
                                            (fs/last-modified-time f)))
                         n)))))
           sort)

      ;; CASE receipts only — a corpus receipt is not part of the hand-written suite.
      case-present (count (filter #(contains? expected-receipts (key %)) receipts))
      case-missing (- case-expected case-present)
      case-proven (count (filter #(and (contains? expected-receipts (key %))
                                       (= :proven (val %)))
                                 receipts))

      ledger-tsv
      (str "# Compatibility ledger — generated by scripts/generate-scorecard.bb; do not edit\n"
           "# tier 1 = proven compatible (differential receipt names this file)\n"
           "# admitted = parses; execution NOT attempted (untrusted corpus). NOT ADR tier 2,\n"
           "#            which requires parsing AND executing — that is NOT ASSESSED here.\n"
           "# tier 3 = rejected (named error code and source position)\n"
           "file\ttier\tcode\tevidence\n"
           (str/join "\n"
                     (map #(str (:file %) "\t" (let [t (:tier %)] (if (keyword? t) (clojure.core/name t) (str t))) "\t"
                                (if (str/blank? (:code %)) "-" (:code %)) "\t"
                                (:evidence %))
                          ledger))
           "\n")

      ;; The rejection REASON, not just the code. All 79 tier-3 files carry the same
      ;; `malformed_syntax` code, which names nothing a reader can act on; the parser
      ;; message behind it does. Grouping by that message turns the corpus into a
      ;; RANKED WORK LIST instead of a wall of identical codes.
      reason-of (fn [{:keys [detail code]}]
                  (-> (or detail "")
                      (str/replace (re-pattern (str "^" (java.util.regex.Pattern/quote (or code "")) " ")) "")
                      (str/replace #"\s*@\d+:\d+\s*$" "")
                      str/trim))

      by-reason (->> (get by-tier 3 [])
                     (group-by reason-of)
                     (map (fn [[r xs]]
                            {:reason (if (str/blank? r) "(no message)" r)
                             :count (count xs)
                             :examples (->> xs (map :file) sort (take 3))}))
                     (sort-by (comp - :count)))

      limitations-md
      (str "# Known limitations\n\n"
           "Generated by `scripts/generate-scorecard.bb` from the compatibility ledger. Do not edit.\n\n"
           "Refusals GROUPED BY the parser's own message, ranked by how many corpus files hit\n"
           "each one. Up to three example files are shown per group — this page is a ranked\n"
           "index, NOT the full list; `docs/COMPATIBILITY-LEDGER.tsv` names every file with its\n"
           "position. A refusal is a limitation stated out loud, and ADR 0001 prefers it to a\n"
           "false success.\n\n"
           ;; Every tier stated explicitly. "The remaining N were admitted" is arithmetic
           ;; that holds only while tier 1 is 0 — the moment a corpus file earns a receipt,
           ;; the remainder after rejections is t1 + t2 and the sentence becomes false.
           "Of " total " corpus files: **" t1 "** proven, **" t2 "** admitted (parsed — NOT a\n"
           "parity claim), **" t3 "** rejected. This page covers the rejected set.\n\n"
           (str/join "\n"
                     (map (fn [{:keys [reason count examples]}]
                            (str "## " reason "\n\n"
                                 "Files: **" count "**\n\n"
                                 (str/join "\n" (map #(str "- `" % "`") examples))
                                 (if (> count 3) (str "\n- …and " (- count 3) " more (see the ledger)") "")
                                 "\n"))
                          by-reason))
           ;; Exactly one trailing newline. Each reason block ends with one, so appending
           ;; another left a blank line at EOF that `git diff --check` rejects — and the
           ;; generator reproduced it faithfully, so `--check` passed while a whitespace
           ;; hook would not. A generator that regenerates a lint failure is a generator
           ;; that makes the lint unfixable.
           "")

      scorecard-md
      (str "# Compatibility scorecard\n\n"
           "Generated by `scripts/generate-scorecard.bb` from evidence on disk. Do not edit.\n\n"
           "ADR 0001 fixes three tiers and forbids collapsing them into one number. "
           "**No compatibility percentage is COMPUTED here**, and every count states its own\n"
           "denominator. The one percentage below is QUOTED from ADR 0001's account of a prior\n"
           "engine — it is the error being avoided, not a measurement of this one. The earlier\n"
           "wording claimed no percentage APPEARED in the document, which the quotation itself\n"
           "falsified: an absolute claim about the text, made in a document containing the\n"
           "counterexample four paragraphs down.\n\n"
           "## Corpus (third-party Jenkinsfiles, parse only)\n\n"
           "| Tier | Meaning | Count |\n|---|---|---|\n"
           "| 1 | proven compatible — a differential receipt names this file | " t1 " of " total " |\n"
           "| 2 | ADR tier 2 (parses **and executes**) | **NOT ASSESSED** — corpus is never executed |\n"
           "| — | admitted (parses only; **not an ADR tier**) | " t2 " of " total " |\n"
           "| 3 | rejected — named error code and source position | " t3 " of " total " |\n\n"
           "**The admitted row is not ADR tier 2.** The ADR requires parsing AND executing; this "
           "scorer only parses, because corpus files are untrusted third-party CI code and are never "
           "run here. Labelling them tier 2 would assert an execution result nobody measured, so ADR "
           "tier 2 is published as NOT ASSESSED.\n\n"
           "**Receipt seals are verified, but not by this generator.** A receipt is counted here by "
           "its verdict line. That line is bound by the seal (FG-161), and the gate recomputes every "
           "seal from the receipt's own content via `--verify-seals` on the differential CLI — where "
           "the hash is computed, rather than reimplemented in a second language, which is how the "
           "three existing copies of the timestamp rule came to disagree. So a doctored receipt fails "
           "the gate; it does not fail this script, and the two are not independent checks.\n\n"
           "**What the seal covers is a SUBSET of what a receipt prints.** EACH RECEIPT STATES ITS OWN unsealed "
           "regions in full, under `## Comparison contract` — this page deliberately does not restate them. It "
           "did, and the two lists drifted apart across four review rounds, each fix completing one copy and "
           "leaving the other short. A doctored receipt fails verification only if the doctoring touched a "
           "sealed field.\n\n"
           "implemented. Each receipt's `## Comparison contract` carries the full list.\n\n"
           "What verification does NOT cover: whether each case on disk still matches the digest its receipt "
           "recorded (freshness, watched by an mtime warning), and the unsealed regions each receipt names.\n\n"
           (if (seq by-code)
             (str "### Tier-3 rejections by code\n\n| Code | Files |\n|---|---|\n"
                  (str/join "\n" (map (fn [[c n]] (str "| `" c "` | " n " |")) by-code))
                  "\n\n")
             "")
           "## Differential case suite (hand-written cases — a DIFFERENT population)\n\n"
           "| Expected | Present | Proven |\n|---|---|---|\n| " case-expected " | " case-present " | "
           case-proven " of " case-expected " |\n\n"
           (if (pos? case-missing)
             (str "**" case-missing " expected receipt(s) MISSING** — a case exists with no receipt. "
                  "The proven count is measured against what the cases expect, so a missing receipt "
                  "shows as a shortfall instead of quietly leaving the fraction.\n\n")
             "")
           "**These two sections do not share a denominator.** Corpus files PROVEN by a receipt: "
           "**" t1 "** of " total ". "
           (if (zero? t1)
             "Every receipt proves a hand-written case, not a corpus file. "
             (str "Those files appear as tier 1 in the corpus table above and are the only "
                  "ones whose parity is proven. "))
           "Reading the receipt count against the corpus "
           "count would produce exactly the false ratio ADR 0001 was written to prevent — the "
           "prior engine's 146 IRs against 5 proven files, which a single percentage would have "
           "reported as 64%.\n")]

  ;; WARNINGS BEFORE THE BRANCH. `build-and-test.sh` runs `--check` only, so emitting
  ;; these in the write branch meant the AUTOMATED path — the one place a stale or
  ;; wrong-core receipt matters — printed nothing while reporting the suite as fully
  ;; proven. The warning existed and the gate could not see it.
  ;;
  ;; They WARN rather than fail: staleness is an mtime signal, and a fresh clone assigns
  ;; arbitrary mtimes, so failing `--check` on it would redden a clean checkout for a
  ;; condition that is not real. FG-164's seal-bound hash is what can justify failing.
  (doseq [n (sort (map key (filter #(= :wrong-core (val %)) receipts)))]
    (println (str "WARN: receipt " n " was produced against a different jenkins-core — not counted as proven")))
  (doseq [n stale-receipts]
    (println (str "WARN: receipt " n " is OLDER than its case — the case was edited after the proof; re-run the suite")))

  (if check?
    (let [lp (fs/file root "docs/COMPATIBILITY-LEDGER.tsv")
          sp (fs/file root "docs/COMPATIBILITY-SCORECARD.md")
          stale (cond-> []
                  (or (not (fs/exists? lp)) (not= ledger-tsv (slurp lp))) (conj "docs/COMPATIBILITY-LEDGER.tsv")
                  (or (not (fs/exists? sp)) (not= scorecard-md (slurp sp))) (conj "docs/COMPATIBILITY-SCORECARD.md")
                  (let [kp (fs/file root "docs/KNOWN-LIMITATIONS.md")]
                    (or (not (fs/exists? kp)) (not= limitations-md (slurp kp)))) (conj "docs/KNOWN-LIMITATIONS.md"))]
      (if (seq stale)
        (do (println "FAIL: generated artifacts are stale — regenerate with scripts/generate-scorecard.bb")
            (doseq [f stale] (println "  " f))
            (System/exit 1))
        (println (str "scorecard artifacts current: tier1=" t1 " admitted=" t2 " tier3=" t3 " of " total
                      " corpus files; " case-proven "/" case-expected " expected case receipts proven"
                      (if (pos? case-missing) (str " — " case-missing " MISSING") "")
))))
    (do
      (spit (fs/file root "docs/COMPATIBILITY-LEDGER.tsv") ledger-tsv)
      (spit (fs/file root "docs/COMPATIBILITY-SCORECARD.md") scorecard-md)
      (spit (fs/file root "docs/KNOWN-LIMITATIONS.md") limitations-md)
      (println (str "wrote docs/COMPATIBILITY-LEDGER.tsv and docs/COMPATIBILITY-SCORECARD.md"))
      (println (str "corpus: tier1=" t1 " admitted(not a tier)=" t2 " tier3=" t3 " of " total))
      (println (str "cases:  " case-proven " proven of " case-expected " expected"
                    (if (pos? case-missing) (str " — " case-missing " MISSING") "")
                    " (separate denominator)")))))
