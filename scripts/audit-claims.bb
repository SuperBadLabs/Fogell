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
      ;; `(* ... *)` counts as a comment too. Missing it was a REGRESSION: the original
      ;; broad line scan saw such claims, and narrowing to `//` let one past `--strict`.
      ;;
      ;; State is carried ACROSS lines, not reset per line. It has to be: both F# block
      ;; comments and F# string literals span lines, and this very repo has
      ;; `"SELECT count(*) FILTER (...` opening a multi-line string in Store.fs. Scanning
      ;; each line fresh would read that continuation line as code, see `(*`, and open a
      ;; block comment that swallows the rest of the file — turning every later line into
      ;; "comment text" where any receipt name could then satisfy any claim. That is
      ;; fail-OPEN, so per-line scanning is not merely imprecise here, it is unsafe.
      ;;
      ;; Returns [spans, mode-at-end-of-line, depth, dollars, holes, code-before-comment?].
      scan-line
      (fn [l mode0 depth0 dollars0 holes0]
        (let [n (count l)
              at (fn [i] (get l i))
              run-of (fn [ch i] (loop [j i] (if (= ch (at j)) (recur (inc j)) (- j i))))]
          ;; `depth` is block-comment NESTING. F# nests `(* ... (* ... *) ... *)`, so
          ;; leaving on the first `*)` drops back to :code mid-comment and the rest of
          ;; the block is read as code — an uncited claim after an inner close would
          ;; slip past `--strict`. Depth is carried across lines with the mode.
          ;;
          ;; SPANS, not one string. Two comments with CODE between them on one line —
          ;; `(* Receipt: X *) let x = 1 (* MEASURED ... *)` — are unrelated, and
          ;; joining them let the first comment's receipt satisfy the second's claim.
          ;; A span closes when code intervenes (`gap?`); adjacent comments separated
          ;; only by whitespace still merge, since nothing separates them semantically.
          ;;
          ;; `holes` is the INTERPOLATED-STRING stack. The `{...}` of `$"..."` is CODE
          ;; — comments are legal inside it, so `$"{1 (* MEASURED *) + 1}"` carries a
          ;; genuine claim — and treating the whole literal as a string hid it from
          ;; `--strict`. A hole pushes `[string-mode brace-count]` and drops to :code;
          ;; a matching run of `}` pops back.
          ;;
          ;; `dollars` is the raw-string DOLLAR COUNT. `$$"""..."""` delimits its holes
          ;; with TWO braces, so with the single-dollar rule `{{` read as an escape and
          ;; a comment inside a `{{...}}` hole was never scanned — an uncited claim
          ;; sliding past `--strict` through the one string form the scanner spelled
          ;; wrong. n dollars → n braces open a hole; below n they are literal text;
          ;; the `{{`/`}}` ESCAPE rule exists only at n = 1.
          (loop [k 0, mode mode0, depth depth0, dollars dollars0, holes holes0, spans [], cur [], gap? false, code? false]
            (let [flush (fn [] (if (seq cur) (conj spans (str/join " " cur)) spans))
                  ;; entering a comment: close the current span first if code intervened
                  enter (fn [] (if (and gap? (seq cur)) [(flush) []] [spans cur]))]
              (if (>= k n)
                [(flush) mode depth dollars holes code?]
                (case mode
                  :code
                  (cond
                    ;; interpolated forms first — `$` must not read as plain code + `"`
                    (= \$ (at k))
                    (let [d (run-of \$ k)]
                      (cond
                        (and (= \" (at (+ k d))) (= \" (at (+ k d 1))) (= \" (at (+ k d 2))))
                        (recur (+ k d 3) :interp-triple depth d holes spans cur true true)
                        (and (= 1 d) (= \@ (at (inc k))) (= \" (at (+ k 2))))
                        (recur (+ k 3) :interp-verbatim depth 1 holes spans cur true true)
                        (and (= 1 d) (= \" (at (inc k))))
                        (recur (+ k 2) :interp depth 1 holes spans cur true true)
                        :else (recur (+ k d) :code depth dollars holes spans cur true true)))
                    (and (= \@ (at k)) (= \$ (at (inc k))) (= \" (at (+ k 2))))
                    (recur (+ k 3) :interp-verbatim depth 1 holes spans cur true true)
                    (and (= \" (at k)) (= \" (at (inc k))) (= \" (at (+ k 2)))) (recur (+ k 3) :triple depth dollars holes spans cur true true)
                    (and (= \@ (at k)) (= \" (at (inc k)))) (recur (+ k 2) :verbatim depth dollars holes spans cur true true)
                    (= \" (at k)) (recur (inc k) :str depth dollars holes spans cur true true)
                    ;; '\n' — escaped char literal
                    (and (= \' (at k)) (= \\ (at (inc k))) (= \' (at (+ k 3)))) (recur (+ k 4) :code depth dollars holes spans cur true true)
                    ;; 'x' — plain char literal, but NOT 'T (a generic type variable)
                    (and (= \' (at k)) (at (inc k)) (not= \\ (at (inc k))) (= \' (at (+ k 2))))
                    (recur (+ k 3) :code depth dollars holes spans cur true true)
                    (and (= \( (at k)) (= \* (at (inc k))))
                    (let [[spans' cur'] (enter)] (recur (+ k 2) :block 1 dollars holes spans' cur' false code?))
                    ;; `//` runs to end of line; `///` must consume all three slashes.
                    (and (= \/ (at k)) (= \/ (at (inc k))))
                    (let [[spans' cur'] (enter)
                          final (conj cur' (str/replace (subs l k) #"^/+\s?" ""))]
                      [(conj spans' (str/join " " final)) mode depth dollars holes code?])
                    ;; hole bookkeeping: a nested `{` re-enters :code so its `}` cannot
                    ;; close the hole early; a run of `}` matching the hole's brace
                    ;; count pops the string's mode back
                    (and (= \{ (at k)) (seq holes)) (recur (inc k) :code depth dollars (conj holes [:code 1]) spans cur true true)
                    (and (= \} (at k)) (seq holes))
                    (let [[m d] (peek holes)]
                      (if (>= (run-of \} k) d)
                        (recur (+ k d) m depth dollars (pop holes) spans cur true true)
                        (recur (inc k) :code depth dollars holes spans cur true true)))
                    :else (let [c? (not (Character/isWhitespace (at k)))]
                            (recur (inc k) :code depth dollars holes spans cur (or gap? c?) (or code? c?))))

                  :block
                  (cond
                    (and (= \( (at k)) (= \* (at (inc k)))) (recur (+ k 2) :block (inc depth) dollars holes spans cur gap? code?)
                    (and (= \* (at k)) (= \) (at (inc k))))
                    (if (<= depth 1)
                      (recur (+ k 2) :code 0 dollars holes spans cur gap? code?)
                      (recur (+ k 2) :block (dec depth) dollars holes spans cur gap? code?))
                    ;; collect the block's text one char at a time; cheap enough for a gate
                    :else (recur (inc k) :block depth dollars holes spans
                                 (conj (vec (butlast cur)) (str (or (last cur) "") (at k)))
                                 gap? code?))

                  :str
                  (cond
                    (= \\ (at k)) (recur (+ k 2) :str depth dollars holes spans cur gap? code?)
                    (= \" (at k)) (recur (inc k) :code depth dollars holes spans cur gap? code?)
                    :else (recur (inc k) :str depth dollars holes spans cur gap? code?))

                  ;; @"..." has no backslash escapes; "" is one literal quote
                  :verbatim
                  (cond
                    (and (= \" (at k)) (= \" (at (inc k)))) (recur (+ k 2) :verbatim depth dollars holes spans cur gap? code?)
                    (= \" (at k)) (recur (inc k) :code depth dollars holes spans cur gap? code?)
                    :else (recur (inc k) :verbatim depth dollars holes spans cur gap? code?))

                  :triple
                  (if (and (= \" (at k)) (= \" (at (inc k))) (= \" (at (+ k 2))))
                    (recur (+ k 3) :code depth dollars holes spans cur gap? code?)
                    (recur (inc k) :triple depth dollars holes spans cur gap? code?))

                  :interp
                  (cond
                    (= \\ (at k)) (recur (+ k 2) :interp depth dollars holes spans cur gap? code?)
                    (and (= \{ (at k)) (= \{ (at (inc k)))) (recur (+ k 2) :interp depth dollars holes spans cur gap? code?)
                    (and (= \} (at k)) (= \} (at (inc k)))) (recur (+ k 2) :interp depth dollars holes spans cur gap? code?)
                    (= \{ (at k)) (recur (inc k) :code depth dollars (conj holes [:interp 1]) spans cur gap? code?)
                    (= \" (at k)) (recur (inc k) :code depth dollars holes spans cur gap? code?)
                    :else (recur (inc k) :interp depth dollars holes spans cur gap? code?))

                  :interp-verbatim
                  (cond
                    (and (= \" (at k)) (= \" (at (inc k)))) (recur (+ k 2) :interp-verbatim depth dollars holes spans cur gap? code?)
                    (and (= \{ (at k)) (= \{ (at (inc k)))) (recur (+ k 2) :interp-verbatim depth dollars holes spans cur gap? code?)
                    (and (= \} (at k)) (= \} (at (inc k)))) (recur (+ k 2) :interp-verbatim depth dollars holes spans cur gap? code?)
                    (= \{ (at k)) (recur (inc k) :code depth dollars (conj holes [:interp-verbatim 1]) spans cur gap? code?)
                    (= \" (at k)) (recur (inc k) :code depth dollars holes spans cur gap? code?)
                    :else (recur (inc k) :interp-verbatim depth dollars holes spans cur gap? code?))

                  :interp-triple
                  (cond
                    (and (= \" (at k)) (= \" (at (inc k))) (= \" (at (+ k 2))))
                    (recur (+ k 3) :code depth dollars holes spans cur gap? code?)
                    ;; the {{ }} ESCAPE exists only at one dollar; at n dollars a run of
                    ;; n braces OPENS the hole and shorter runs are literal text
                    (and (= 1 dollars) (= \{ (at k)) (= \{ (at (inc k)))) (recur (+ k 2) :interp-triple depth dollars holes spans cur gap? code?)
                    (and (= 1 dollars) (= \} (at k)) (= \} (at (inc k)))) (recur (+ k 2) :interp-triple depth dollars holes spans cur gap? code?)
                    (and (= \{ (at k)) (>= (run-of \{ k) dollars))
                    (recur (+ k dollars) :code depth dollars (conj holes [:interp-triple dollars]) spans cur gap? code?)
                    :else (recur (inc k) :interp-triple depth dollars holes spans cur gap? code?))))))))

      ;; One fold per FILE, carrying the scanner's mode AND block-comment depth. Produces
      ;; a vector parallel to `lines`: {:spans [comment spans] :code? code-on-the-line}.
      scan-file
      (fn [lines]
        (loop [ls lines, mode :code, depth 0, dollars 1, holes [], out []]
          (if (empty? ls)
            out
            (let [[spans mode' depth' dollars' holes' code?] (scan-line (first ls) mode depth dollars holes)]
              ;; :continues? — the line STARTED inside a block comment, so even a blank
              ;; line there is comment interior. Without it, a blank line inside one
              ;; multiline (* ... *) produced no spans, read as "not a comment", and
              ;; broke the claim's block in half — rejecting a receipt cited in the
              ;; same syntactic comment as its claim.
              (recur (rest ls) mode' depth' dollars' holes'
                     (conj out {:spans spans :code? code? :continues? (= mode :block)}))))))

      ;; A block may only grow across FULL-LINE comments. Accepting trailing comments as
      ;; claims (above) does not make them block members: two unrelated code lines that
      ;; each carry a trailing comment would otherwise merge, letting `let b = 2 // Receipt: foo`
      ;; satisfy a claim on `let a = 1 // MEASURED ...`. That is exactly the adjacent-claim
      ;; bypass the block logic exists to prevent, re-entering through the new door.
      ;; Derived from the scan rather than a regex, so `(* ... *)` on its own line counts
      ;; as a full comment exactly as `//` does.
      full-comment? (fn [s] (and (or (seq (:spans s)) (:continues? s)) (not (:code? s))))

      ;; One pass. These used to be two near-identical loops, and they had already
      ;; drifted — the unproven counter compared WHOLE LINES where the finder compared
      ;; comment text, so a receipt named in adjacent code could silently resolve a
      ;; claim on one path but not the other. Duplicated rules diverge; this one cannot.
      claims
      (for [f sources
            :let [lines (str/split-lines (slurp f))
                  v (scan-file lines)]
            [i line] (map-indexed vector lines)
            ;; One claim PER SPAN, and only the claim's own span represents this line in
            ;; its block — never a sibling span that code separates from it.
            own (filter #(str/includes? % "MEASURED") (:spans (v i)))
            ;; The receipt must be cited by THIS claim's own contiguous comment block, not
            ;; merely somewhere within twenty lines. A fixed window let a receipt named by a
            ;; NEIGHBOURING claim satisfy this one, so `--strict` could pass with an
            ;; unbacked claim sitting next to a backed one — defeating the per-claim
            ;; guarantee the check exists to give.
            ;; A TRAILING claim — code on its own line — gets NO neighbours at all: its
            ;; receipt must sit in its own span. The code separates it from the comment
            ;; lines around it just as surely as it separates two spans on one line, and
            ;; extending the block anyway let `// Receipt: foo` on the NEXT line satisfy
            ;; `let x = 1 // MEASURED ...` — the adjacent-claim bypass yet again, entered
            ;; this time from the claim's side rather than the citation's.
            :let [expand? (not (:code? (v i)))
                  start (if expand?
                          (loop [k i] (if (and (pos? k) (full-comment? (v (dec k)))) (recur (dec k)) k))
                          i)
                  stop (if expand?
                         (loop [k i] (if (and (< (inc k) (count v)) (full-comment? (v (inc k)))) (recur (inc k)) k))
                         i)
                  ;; COMMENT TEXT only. Including whole lines let a receipt named in
                  ;; adjacent CODE satisfy a claim, which is the same hole as the fixed
                  ;; window, one layer down.
                  neighbours (concat (subvec v start i) (subvec v (inc i) (inc stop)))
                  block (str/join " " (concat (mapcat :spans neighbours) [own]))
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
