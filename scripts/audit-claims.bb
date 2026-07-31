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
      ;;
      ;; The scan tracks F#'s FOUR literal forms, not just ordinary strings. A first
      ;; version treated every `"` as opening one, so `let quote = '"' // MEASURED ...`
      ;; left the scanner stuck inside a string and dropped the claim — an uncited
      ;; claim slipping past `--strict` again, one layer down.
      ;;
      ;; `'` is only a char literal when the closing quote sits where a char literal
      ;; puts it. F# also writes generic type variables as `'T`, and skipping to the
      ;; next `'` there would swallow the rest of the line.
      ;;
      ;; LIMITATION: this is line-based, so a triple-quoted or verbatim string spanning
      ;; several lines leaves its continuation lines scanned as code. That direction is
      ;; fail-CLOSED — it can only invent a claim, never hide one — which is the side to
      ;; err on for a gate.
      comment-text
      (fn [l]
        (let [n (count l)
              at (fn [i] (get l i))]
          (loop [k 0, mode :code]
            (if (>= k n)
              nil
              (case mode
                :code
                (cond
                  (and (= \" (at k)) (= \" (at (inc k))) (= \" (at (+ k 2)))) (recur (+ k 3) :triple)
                  (and (= \@ (at k)) (= \" (at (inc k)))) (recur (+ k 2) :verbatim)
                  (= \" (at k)) (recur (inc k) :str)
                  ;; '\n' — escaped char literal
                  (and (= \' (at k)) (= \\ (at (inc k))) (= \' (at (+ k 3)))) (recur (+ k 4) :code)
                  ;; 'x' — plain char literal, but NOT 'T (a generic type variable)
                  (and (= \' (at k)) (at (inc k)) (not= \\ (at (inc k))) (= \' (at (+ k 2)))) (recur (+ k 3) :code)
                  ;; `///` must consume all three slashes, not two
                  (and (= \/ (at k)) (= \/ (at (inc k)))) (str/replace (subs l k) #"^/+\s?" "")
                  :else (recur (inc k) :code))

                :str
                (cond
                  (= \\ (at k)) (recur (+ k 2) :str)
                  (= \" (at k)) (recur (inc k) :code)
                  :else (recur (inc k) :str))

                ;; @"..." has no backslash escapes; "" is one literal quote
                :verbatim
                (cond
                  (and (= \" (at k)) (= \" (at (inc k)))) (recur (+ k 2) :verbatim)
                  (= \" (at k)) (recur (inc k) :code)
                  :else (recur (inc k) :verbatim))

                :triple
                (if (and (= \" (at k)) (= \" (at (inc k))) (= \" (at (+ k 2))))
                  (recur (+ k 3) :code)
                  (recur (inc k) :triple)))))))

      ;; A block may only grow across FULL-LINE comments. Accepting trailing comments as
      ;; claims (above) does not make them block members: two unrelated code lines that
      ;; each carry a trailing comment would otherwise merge, letting `let b = 2 // Receipt: foo`
      ;; satisfy a claim on `let a = 1 // MEASURED ...`. That is exactly the adjacent-claim
      ;; bypass the block logic exists to prevent, re-entering through the new door.
      full-comment? (fn [l] (some? (re-find #"^\s*//" l)))

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
            :let [start (loop [k i] (if (and (pos? k) (full-comment? (v (dec k)))) (recur (dec k)) k))
                  stop (loop [k i] (if (and (< (inc k) (count v)) (full-comment? (v (inc k)))) (recur (inc k)) k))
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
