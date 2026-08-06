#!/usr/bin/env bb
;; FG-162. Board rows that quote a generated count are RE-DERIVED from the committed
;; ledger and fail on drift — a sweep with a test, not a resolution to be careful.
;;
;; Evidence for existing: eleven-plus instances in one session of a claim and its
;; subject living in different files while only the edited one was updated, including
;; a board row saying "tier2=149" for one commit after the scorecard stopped saying
;; it, and three consecutive corrections to a single FG-160 note — every one to a
;; label or denominator around numbers that never changed. Where a duplicated claim
;; was collapsed into one source the class died immediately; this is that collapse
;; for board numbers.
;;
;; WHAT IT CHECKS, exactly: tokens of the form `tier1=N`, `tier3=N`, `admitted=N`
;; (optional ** bolding) in docs/EXECUTION_BOARD.md, compared against counts derived
;; from docs/COMPATIBILITY-LEDGER.tsv. A `tier2=N` token is refused outright — ADR
;; tier 2 is published as NOT ASSESSED, so no live claim may use it.
;;
;; WHAT IT DOES NOT CHECK, stated so a pass is not misread:
;;   - prose numbers not in token form ("149 files", "of 228") — unverifiable without
;;     guessing which measurement a bare number refers to; rows wanting coverage use
;;     the token form
;;   - FRESHNESS of the ledger itself — that is `generate-scorecard.bb --check`, which
;;     runs only where the corpus is mounted. This checks board-vs-ledger CONSISTENCY
;;     and runs everywhere, CI included, because both files are committed.
;;   - tokens immediately preceded by a double quote — those are retractions quoting
;;     what a row USED to say, and flagging quoted history would punish the honesty
;;     this board practises.
;;
;;   usage: scripts/audit-board-numbers.bb [board-file ledger-file]
;;          The optional paths exist for `prove-board-numbers.sh`, which runs this
;;          against mutated scratch copies — a checker never proven against known-bad
;;          state is indistinguishable from a broken one (the FG-158 lesson; my first
;;          proofs here were manual one-offs the gate could not re-run).

(require '[babashka.fs :as fs] '[clojure.string :as str])

(let [root (str (fs/parent (fs/parent (fs/absolutize *file*))))
      [board-arg ledger-arg] *command-line-args*
      ledger-file (if ledger-arg (fs/file ledger-arg) (fs/file root "docs/COMPATIBILITY-LEDGER.tsv"))
      board-file (if board-arg (fs/file board-arg) (fs/file root "docs/EXECUTION_BOARD.md"))]

  (when-not (fs/exists? ledger-file)
    (println "FAIL: docs/COMPATIBILITY-LEDGER.tsv missing — board numbers cannot be derived")
    (System/exit 1))

  ;; The board got no such check, so a wrong path threw a raw stack trace out of a
  ;; check that runs unconditionally in the gate — a crash reads as a broken gate, not
  ;; as a stated refusal. Same clean failure as the ledger above.
  (when-not (fs/exists? board-file)
    (println (str "FAIL: board file not found: " (str board-file)))
    (System/exit 1))

  (let [tiers (->> (str/split-lines (slurp ledger-file))
                   (remove #(or (str/blank? %) (str/starts-with? % "#") (str/starts-with? % "file\t")))
                   (map #(second (str/split % #"\t" -1))))
        derived {"tier1" (count (filter #(= "1" %) tiers))
                 "tier3" (count (filter #(= "3" %) tiers))
                 "admitted" (count (filter #(= "admitted" %) tiers))}
        board (slurp board-file)

        findings
        (concat
         ;; live tier2= claims are refused: the tier is published as NOT ASSESSED
         (->> (re-seq #"([\"]?)tier2=\*{0,2}(\d+)" board)
              (keep (fn [[_ q n]]
                      (when (not= q "\"")
                        (str "tier2=" n " — ADR tier 2 is NOT ASSESSED; no live claim may use this token")))))
         ;; tier1= / tier3= / admitted= must match the ledger
         (->> (re-seq #"([\"]?)(tier1|tier3|admitted)=\*{0,2}(\d+)\*{0,2}" board)
              (keep (fn [[_ q kind n]]
                      (let [want (get derived kind)]
                        (when (and (not= q "\"") (not= (parse-long n) want))
                          (str kind "=" n " — the ledger derives " kind "=" want)))))))]

    (if (seq findings)
      (do (println (str "BOARD-NUMBER AUDIT FAILED (" (count findings) "):"))
          (doseq [f findings] (println "  " f))
          (println "Fix the board row, or regenerate the ledger if the board is right.")
          (System/exit 1))
      (println (str "board numbers consistent with the ledger: tier1=" (derived "tier1")
                    " tier3=" (derived "tier3") " admitted=" (derived "admitted")
                    " (quoted retractions exempt; prose numbers unchecked)")))))
