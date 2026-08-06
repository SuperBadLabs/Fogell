#!/usr/bin/env bb
;; FG-090/091/092. Generates the compatibility scorecard, the machine-readable
;; ledger, and KNOWN-LIMITATIONS.md — from evidence on disk, never by hand.
;;
;; ADR 0001 fixes three tiers and forbids collapsing them into one number, with the
;; reason recorded: a prior engine had 146 non-empty IRs and 5 files of proven
;; parity, and a single percentage would have implied 64%.
;;
;; THE SAME TRAP IS LIVE IN THIS REPO RIGHT NOW. There are 118 differential receipts
;; and 228 corpus files, and the NAME OVERLAP BETWEEN THEM IS ZERO: every receipt is
;; for a hand-written case in `differential/cases`, not for a corpus Jenkinsfile.
;; Printing "118 proven" anywhere near "228 files" invites 118/228 = 52%, and the
;; true corpus figure for proven parity is 0 of 228. The two populations are
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
      receipts (->> (fs/glob (fs/file root "differential/receipts") "*.receipt.txt")
                    (map (fn [p]
                           (let [n (str/replace (fs/file-name p) ".receipt.txt" "")
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
                             [n (if (re-find #"(?m)^VERDICT: PROVEN \(tier 1\)(?:\s+—[^\n]*)?$" body)
                                    :proven
                                    :other)])))
                    (into {}))

      ;; REJECTION WINS OVER AN OLD RECEIPT. A receipt proves what the engine did when
      ;; it ran; if the CURRENT engine cannot parse the file, "proven compatible" is a
      ;; claim about a binary that no longer exists. The receipt branch used to come
      ;; first, so a parser regression on a receipted file would have published it as
      ;; tier 1 — unreachable today (no corpus file has a receipt) and wrong by
      ;; construction, the same correct-by-accident shape as the PROVEN-PARTIAL match.
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

      case-receipts (count receipts)
      case-proven (count (filter #(= :proven (val %)) receipts))

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
           "**Receipt seals are not verified by this generator.** A receipt is counted by its verdict "
           "line; nothing here recomputes the seal that binds its result, output and workspace "
           "evidence (ADR 0004). Reimplementing that hash in a second language is how the three "
           "existing copies of the timestamp rule came to disagree, so the gap is stated and filed "
           "(FG-161) rather than papered over with a weaker check.\n\n"
           (if (seq by-code)
             (str "### Tier-3 rejections by code\n\n| Code | Files |\n|---|---|\n"
                  (str/join "\n" (map (fn [[c n]] (str "| `" c "` | " n " |")) by-code))
                  "\n\n")
             "")
           "## Differential case suite (hand-written cases — a DIFFERENT population)\n\n"
           "| Receipts | Proven |\n|---|---|\n| " case-receipts " | " case-proven " of " case-receipts " |\n\n"
           "**These two sections do not share a denominator.** The name overlap between the "
           "corpus and the receipt set is currently **zero**: every receipt proves a "
           "hand-written case, not a corpus file. Reading the receipt count against the corpus "
           "count would produce exactly the false ratio ADR 0001 was written to prevent — the "
           "prior engine's 146 IRs against 5 proven files, which a single percentage would have "
           "reported as 64%.\n")]

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
                      " corpus files; " case-proven "/" case-receipts " case receipts proven"))))
    (do
      (spit (fs/file root "docs/COMPATIBILITY-LEDGER.tsv") ledger-tsv)
      (spit (fs/file root "docs/COMPATIBILITY-SCORECARD.md") scorecard-md)
      (spit (fs/file root "docs/KNOWN-LIMITATIONS.md") limitations-md)
      (println (str "wrote docs/COMPATIBILITY-LEDGER.tsv and docs/COMPATIBILITY-SCORECARD.md"))
      (println (str "corpus: tier1=" t1 " admitted(not a tier)=" t2 " tier3=" t3 " of " total))
      (println (str "cases:  " case-proven " proven of " case-receipts " receipts (separate denominator)")))))
