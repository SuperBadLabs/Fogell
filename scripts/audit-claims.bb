#!/usr/bin/env bb
;; FG-104. Every code comment that asserts MEASURED Jenkins behaviour must name the
;; receipt that proves it.
;;
;; Three times a comment of mine became the specification the code silently disagreed
;; with: `resolveName` promising `env['X']` support, a case comment asserting Jenkins does
;; not mask usernames (a reviewer cited it and was wrong because I was), and `#0`
;; provenance documented but never produced. A claim nobody can check is not evidence, it
;; is a rumour with a citation style.
;;
;; This makes the rule mechanical: a MEASURED claim naming no existing receipt is a defect.
;;
;; WHAT IT CANNOT DO, stated so nobody mistakes a pass for proof: it checks that a receipt
;; is NAMED, never that the named receipt EXERCISES the claim. Its first review found three
;; citations of mine pointing at receipts that covered only part of the sentence — a
;; post-order claim citing a build-#1 receipt for `fixed`/`regression` slots it never
;; exercises, an options claim covering pipeline AND stage citing only the pipeline one, and
;; a narration claim covering timeouts AND branch failures citing only the timeout. The
;; check is a floor, not a ceiling; a human still has to open the receipt.
;;
;;   usage: scripts/audit-claims.bb [--strict]
;;          --strict exits non-zero when any claim is unbacked

(require '[babashka.fs :as fs] '[clojure.string :as str])

(let [strict? (some #{"--strict"} *command-line-args*)
      root (str (fs/parent (fs/parent (fs/absolutize *file*))))
      receipts (->> (fs/glob (str root "/differential/receipts") "*.receipt.txt")
                    (map #(str/replace (fs/file-name %) ".receipt.txt" ""))
                    set)
      ;; ALL F# sources, not just src/ — tools and tests carry MEASURED claims too, and a
      ;; check whose scope is narrower than its description is the very defect this script
      ;; exists to catch. Caught by review, in the script that catches it.
      sources (->> (concat (fs/glob (str root "/src") "**/*.fs")
                           (fs/glob (str root "/tools") "**/*.fs")
                           (fs/glob (str root "/tests") "**/*.fs"))
                   (map str)
                   (remove #(str/includes? % "/obj/"))
                   (remove #(str/includes? % "/bin/"))
                   sort)
      findings
      (for [f sources
            :let [lines (str/split-lines (slurp f))]
            [i line] (map-indexed vector lines)
            ;; A COMMENT asserting MEASURED. Matching the word anywhere also matched CODE —
            ;; a string literal or identifier containing it — so the gate could block on a
            ;; line that asserts nothing. Found on PR #22 review 4, after the merge.
            :when (and (str/includes? line "MEASURED") (re-find #"^\s*(?://|///)" line))
            ;; The receipt must be cited by THIS claim's own contiguous comment block, not
            ;; merely somewhere within twenty lines. A fixed window let a receipt named by a
            ;; NEIGHBOURING claim satisfy this one, so `--strict` could pass with an
            ;; unbacked claim sitting next to a backed one — defeating the per-claim
            ;; guarantee the check exists to give.
            :let [comment? (fn [l] (re-find #"^\s*(///|//)" l))
                  v (vec lines)
                  start (loop [k i] (if (and (pos? k) (comment? (v (dec k)))) (recur (dec k)) k))
                  stop (loop [k i] (if (and (< (inc k) (count v)) (comment? (v (inc k)))) (recur (inc k)) k))
                  ;; COMMENT TEXT only. Including whole lines let a receipt named in
                  ;; adjacent CODE satisfy a claim, which is the same hole as the fixed
                  ;; window, one layer down.
                  block (->> (subvec v start (inc stop))
                             (keep #(second (re-find #"^\s*(?://|///)\s?(.*)$" %)))
                             (str/join " "))
                  named (filter #(str/includes? block %) receipts)
                  ;; An explicit UNPROVEN admission resolves the claim too — some Jenkins
                  ;; behaviours cannot be receipted without over-fitting (a REJECTION makes
                  ;; both engines fail, leaving only narration to compare). Saying so is a
                  ;; valid answer; saying nothing is not. They are counted separately so the
                  ;; admission stays visible instead of quietly passing.
                  unproven? (str/includes? block "UNPROVEN")]
            :when (and (empty? named) (not unproven?))]
        {:file (str (fs/relativize root f)) :line (inc i)
         :text (str/trim (subs line 0 (min 100 (count line))))})

      unproven-count
      (count (for [f sources
                   :let [lines (str/split-lines (slurp f))]
                   [i line] (map-indexed vector lines)
                   :when (and (str/includes? line "MEASURED")
                              (re-find #"^\s*(?://|///)" line))
                   :let [comment? (fn [l] (re-find #"^\s*(///|//)" l))
                         v (vec lines)
                         start (loop [k i] (if (and (pos? k) (comment? (v (dec k)))) (recur (dec k)) k))
                         stop (loop [k i] (if (and (< (inc k) (count v)) (comment? (v (inc k)))) (recur (inc k)) k))
                         blk (str/join " " (subvec v start (inc stop)))]
                   :when (str/includes? blk "UNPROVEN")]
               1))]

  (println (format "MEASURED claims: %d source files scanned, %d receipts available"
                   (count sources) (count receipts)))
  (if (empty? findings)
    (println (format "every MEASURED claim resolves: cited by a receipt, or admitted UNPROVEN (%d)"
                     unproven-count))
    (do
      (println (format "\n%d claim(s) neither cite a receipt nor admit UNPROVEN (%d admitted):\n"
                       (count findings) unproven-count))
      (doseq [{:keys [file line text]} findings]
        (println (format "  %s:%d\n    %s" file line text)))
      (println "\nEach must either cite its receipt, or say it is UNPROVEN.")))
  (when (and strict? (seq findings)) (System/exit 1)))
