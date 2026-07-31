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
      ;; The COMMENT PORTION of a line, or nil. Requiring the line to BEGIN with `//`
      ;; was itself a defect: `let mode = x  // MEASURED on Jenkins: ...` is a genuine
      ;; claim, and demanding a full-line comment let it bypass the gate entirely.
      ;; That over-correction was introduced while fixing the opposite hole — matching
      ;; MEASURED anywhere, including in CODE — so both directions are handled here:
      ;; the text after `//`, and only when the `//` is not inside a string literal.
      ;; A URL like "http://x" therefore contributes nothing, and neither does an
      ;; identifier that happens to contain the word.
      comment-text
      (fn [l]
        (loop [k 0, in-str? false]
          (cond
            (>= k (count l)) nil
            in-str? (recur (inc k) (not (and (= \" (nth l k)) (not= \\ (nth l (dec k))))))
            (= \" (nth l k)) (recur (inc k) true)
            (and (= \/ (nth l k)) (= \/ (get l (inc k))) )
            (str/replace (subs l k) #"^/+\s?" "")
            :else (recur (inc k) false))))

      ;; One pass. These used to be two near-identical loops, and they had already
      ;; drifted — the unproven counter compared WHOLE LINES where the finder compared
      ;; comment text, so a receipt named in adjacent code could silently resolve a
      ;; claim on one path but not the other. Duplicated rules diverge; this one cannot.
      claims
      (for [f sources
            :let [lines (str/split-lines (slurp f))
                  v (vec lines)]
            [i line] (map-indexed vector lines)
            :let [own (comment-text line)]
            :when (and own (str/includes? own "MEASURED"))
            ;; The receipt must be cited by THIS claim's own contiguous comment block, not
            ;; merely somewhere within twenty lines. A fixed window let a receipt named by a
            ;; NEIGHBOURING claim satisfy this one, so `--strict` could pass with an
            ;; unbacked claim sitting next to a backed one — defeating the per-claim
            ;; guarantee the check exists to give.
            :let [start (loop [k i] (if (and (pos? k) (comment-text (v (dec k)))) (recur (dec k)) k))
                  stop (loop [k i] (if (and (< (inc k) (count v)) (comment-text (v (inc k)))) (recur (inc k)) k))
                  ;; COMMENT TEXT only. Including whole lines let a receipt named in
                  ;; adjacent CODE satisfy a claim, which is the same hole as the fixed
                  ;; window, one layer down.
                  block (->> (subvec v start (inc stop)) (keep comment-text) (str/join " "))
                  named (filter #(str/includes? block %) receipts)
                  ;; An explicit UNPROVEN admission resolves the claim too — some Jenkins
                  ;; behaviours cannot be receipted without over-fitting (a REJECTION makes
                  ;; both engines fail, leaving only narration to compare). Saying so is a
                  ;; valid answer; saying nothing is not. They are counted separately so the
                  ;; admission stays visible instead of quietly passing.
                  unproven? (str/includes? block "UNPROVEN")]]
        {:file (str (fs/relativize root f)) :line (inc i)
         :backed? (or (seq named) unproven?) :unproven? unproven?
         :text (str/trim (subs line 0 (min 100 (count line))))})

      findings (remove :backed? claims)
      unproven-count (count (filter :unproven? claims))]

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
