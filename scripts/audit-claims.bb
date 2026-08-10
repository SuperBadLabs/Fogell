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
      ;; APPROVAL-LANE SCENARIOS COUNT AS EVIDENCE, and a receipt cannot replace them.
      ;; A receipt compares terminal result, normalised output and workspace hash; it
      ;; CANNOT observe whether an approval PROMPT WAS PUBLISHED. FG-141/142/143 were
      ;; all invisible to a green 115/115 suite for exactly that reason. Forcing those
      ;; claims to say UNPROVEN would make the honest answer a lie and drain the word
      ;; of meaning for the claims that genuinely are unproven.
      ;;
      ;; The citation is VERIFIED, not free text: the scenario letter must exist in
      ;; `run-approval-lane.sh`, so `approval-lane scenario Q` resolves and a citation
      ;; of a scenario nobody wrote does not.
      lane-scenarios (let [f (fs/file root "scripts/run-approval-lane.sh")]
                       (if (fs/exists? f)
                         (->> (re-seq #"(?m)^echo \"=== ([A-Z][0-9]*):" (slurp f))
                              (map second)
                              (map #(str "approval-lane scenario " %))
                              set)
                         #{}))
      ;; WHOLE-TOKEN citations. A citation must be bounded on BOTH sides by something
      ;; outside [A-Za-z0-9_-].
      ;;
      ;; Twice now the word for this check has been ahead of the check. It was called
      ;; a KNOWN FLOOR while `scenario Z` stood in for `scenario Z2` — and two comments
      ;; promptly did exactly that. It was then called EXACT while the boundary was
      ;; `(?![A-Za-z0-9])`, which a HYPHEN slips straight through: `credentials-userpass`
      ;; matched inside `credentials-userpass-masking`, and this corpus has FIVE such
      ;; prefix pairs, so a stale hyphen-suffixed citation resolved against the shorter
      ;; receipt. Both boundaries, and `-`/`_` counted as name characters.
      ;;
      ;; What it still CANNOT do, so a pass is not mistaken for proof: it verifies a
      ;; cited scenario EXISTS, never that the scenario EXERCISES the claim — the
      ;; same ceiling the receipt citations have.
      ;; PROOF-SCRIPT CASES ARE EVIDENCE TOO, for the same reason lane scenarios are:
      ;; some properties no receipt can carry. `stage-input-directive` is refused by
      ;; Fogell and ACCEPTED by Jenkins, so a differential case is NOT-COMPARABLE by
      ;; construction — there is no receipt to cite and never will be. Verified the same
      ;; way: the case name must actually appear as an assertion in the proof script.
      proof-cases (let [f (fs/file root "scripts/prove-section-refusals.sh")]
                    (if (fs/exists? f)
                      (->> (re-seq #"(?m)^expect_(?:refusal|control|env_ok)\s+([a-z0-9-]+)" (slurp f))
                           (map second)
                           set)
                      #{}))
      citable (into (into receipts lane-scenarios) proof-cases)
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
                        ;; d is ALSO the enclosing string's dollar count — restore it.
                        ;; A nested $\"\"\" inside a $$\"\"\" hole set dollars to 1, and
                        ;; without the restore the outer string's next {{...}} hole was
                        ;; read as escaped text, hiding any claim inside it.
                        (recur (+ k d) m depth d (pop holes) spans cur true true)
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
                  named (filter #(re-find (re-pattern (str "(?<![A-Za-z0-9_-])\\Q" % "\\E(?![A-Za-z0-9_-])"))
                                          block)
                                citable)
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
      unproven-count (count (filter :unproven? claims))

      ;; SECOND CHECK: A CITATION MUST NAME SOMETHING THAT EXISTS.
      ;;
      ;; The check above asks "does this MEASURED claim cite anything citable" and is
      ;; therefore blind in two directions at once. A comment reading
      ;; `Receipt: script-sh-returnstdout` — naming a receipt nobody ever wrote — sailed
      ;; through it TWICE: the sentence said "measured" in lower case so it was never
      ;; examined as a claim at all, and even had it been, the rule only requires SOME
      ;; citable name in the block, never that every name mentioned resolves.
      ;;
      ;; That was the SEVENTH documentation overclaim found by review on one branch. Six
      ;; earlier ones were answered with a careful sentence and the seventh arrived
      ;; anyway, which is the signal that the answer was never a sentence. A dangling
      ;; citation is mechanically decidable, so it should not cost a review round.
      ;;
      ;; This deliberately does NOT key on `MEASURED`. Keying on a word I have to
      ;; remember to shout is how the last one escaped; a citation is a citation whatever
      ;; sentence surrounds it.
      ;;
      ;; NAME SHAPE, not any token: lower-case kebab with at least one hyphen, which all
      ;; 129 receipt names have. Requiring the hyphen is what keeps prose out — "the
      ;; receipt that proves it" and "receipt whose seal" cannot match, so the pattern
      ;; needs no list of stop-words to maintain.
      ;;
      ;; WHAT IT CANNOT DO, in the same spirit as the ceiling stated at the top: it
      ;; verifies the name RESOLVES, never that the receipt exercises the sentence. A
      ;; citation of a real but irrelevant receipt still passes, and a human still has to
      ;; open it.
      ;; A CITATION IS BACKTICKED, OR INTRODUCED BY A COLON. Bare prose after the word
      ;; "receipt" is not a citation and must not be read as one: "left a re-run receipt
      ;; byte-identical to a first-attempt pass" is an English sentence, and an earlier
      ;; draft of this check reported `byte-identical` as a missing receipt. A checker
      ;; that cries wolf on prose gets switched off, so the trigger is the punctuation an
      ;; actual citation always carries.
      cite-backticked #"(?i)receipts?\s+((?:`[^`\n]+`(?:\s*(?:,|and|or)\s*)?)+)"
      cite-colon #"(?i)receipts?:\s*((?:`?[a-z][a-z0-9./*-]*`?(?:\s*,\s*)?)+)"
      ;; `.b1` and a trailing `-*` are part of the name as written — see `resolves?`.
      ;;
      ;; TWO ALTERNATIVES, and the glob one is not decoration. A single hyphenless
      ;; family — `fam-*` — has no `-[a-z0-9]` group before the star, so a
      ;; hyphen-requiring pattern never extracts it and the citation is silently
      ;; UNCHECKED. Proven: the "glob matching nothing" arm passed until this was split
      ;; in two. The hyphen stays required for the non-glob form, because that is what
      ;; keeps ordinary prose words out of a backticked list.
      ;; `[./]+` and not `[./]`: a COMPACT citation writes the second build as
      ;; `multi-case.b1/.b9`, where the separator is `/.` — two characters. Requiring
      ;; one stopped the match at `multi-case.b1` and the `.b9` was never extracted,
      ;; so a citation naming a build that does not exist passed while this check
      ;; claimed every named receipt was verified. Raised in review on PR #53.
      cite-name #"[a-z][a-z0-9]*(?:-[a-z0-9]+)*-?\*|[a-z][a-z0-9]*(?:-[a-z0-9]+)+(?:[./]+[a-z0-9]+)*"
      ;; TWO REAL SPELLINGS, learned from the citations already in the tree rather than
      ;; assumed — the first draft called six of them dangling and every one was mine
      ;; being wrong about the naming, not the comment being wrong:
      ;;   - A MULTI-BUILD case stores one receipt PER BUILD, so the case
      ;;     `git-step-refetch` is on disk as `git-step-refetch.b1` and `.b2`. Comments
      ;;     cite the case; both spellings must resolve, or the check would demand
      ;;     comments name a build number that means nothing to the reader.
      ;;   - A FAMILY is cited as a glob: `checkout-scm-*` covers four receipts. That is
      ;;     the honest way to cite four things, so it resolves when the prefix matches
      ;;     something — and still fails when it matches nothing.
      ;; A compact citation names SEVERAL receipts: `multi-case.b1/.b9` is `.b1` AND
      ;; `.b9` on one base. Expanding it here means the resolver below answers for one
      ;; name at a time and cannot silently check only the first.
      expand-citation
      (fn [tok]
        (let [parts (str/split tok #"/")]
          (if (< (count parts) 2)
            [tok]
            (let [base (str/replace (first parts) #"\.b\d+$" "")]
              (cons (first parts)
                    (map (fn [p] (if (str/starts-with? p ".") (str base p) p)) (rest parts)))))))

      resolves?
      (fn [tok]
        ;; `.receipt.txt` is the third real spelling: some comments cite the FILE.
        (let [glob? (str/ends-with? tok "*")
              t (-> tok (str/replace #"\*$" "") (str/replace #"\.receipt\.txt$" ""))
              ;; AN EXPLICIT BUILD NUMBER IS CHECKED EXACTLY. A first version resolved
              ;; any `.bN` through its family, so `multi-case.b9` passed on the strength
              ;; of `multi-case.b1` existing — a citation pointing at a build that was
              ;; never run, which is the same defect as a missing receipt wearing a
              ;; plausible name. Proven by the "typo'd build suffix" arm.
              explicit-build? (re-find #"\.b\d+$" t)]
          (cond
            glob? (some #(str/starts-with? % t) citable)
            explicit-build? (contains? citable t)
            ;; A bare case name resolves either directly or through its per-build
            ;; receipts — comments cite the CASE, and making them name a build number
            ;; would serve the checker at the reader's expense.
            :else (or (contains? citable t)
                      (some #(str/starts-with? % (str t ".")) citable)))))
      ;; MATCHED OVER THE WHOLE COMMENT BLOCK, not one line. These comments wrap, so
      ;; `cited receipts` routinely ends a line and the name begins the next one; a
      ;; per-line matcher misses exactly those and its coverage then depends on where
      ;; the text happened to wrap. Measured while writing this: the per-line version
      ;; found 5 and the block version finds more, all of them real.
      ;;
      ;; A block is a run of FULL-LINE comments, reusing the same `full-comment?` the
      ;; claims check uses so the two cannot drift. A TRAILING comment is its own block:
      ;; code separates it from its neighbours, the same rule applied there.
      blocks
      (for [f sources
            :let [lines (str/split-lines (slurp f))
                  v (scan-file lines)
                  n (count v)]
            [start _] (map-indexed vector lines)
            :when (and (seq (:spans (v start)))
                       ;; `zero?` FIRST — `or` is left to right and `(v -1)` throws.
                       (or (zero? start)
                           (:code? (v start))
                           (not (full-comment? (v (dec start))))))
            :let [stop (if (:code? (v start))
                         start
                         (loop [k start]
                           (if (and (< (inc k) n) (full-comment? (v (inc k)))) (recur (inc k)) k)))]]
        {:file f
         :line (inc start)
         :text (str/join " " (mapcat :spans (subvec v start (inc stop))))})

      dangling
      (for [{:keys [file line text]} blocks
            [_ lst] (concat (re-seq cite-backticked text) (re-seq cite-colon text))
            token (re-seq cite-name lst)
            name (expand-citation token)
            :when (not (resolves? name))]
        {:file (str (fs/relativize root file)) :line line :name name})]

  (println (format "MEASURED claims: %d source files scanned, %d receipts + %d lane scenarios + %d proof cases citable"
                   (count sources) (count receipts) (count lane-scenarios) (count proof-cases)))
  (if (empty? findings)
    (println (format "every MEASURED claim resolves: cited by a receipt, or admitted UNPROVEN (%d)"
                     unproven-count))
    (do
      (println (format "\n%d claim(s) neither cite a receipt nor admit UNPROVEN (%d admitted):\n"
                       (count findings) unproven-count))
      (doseq [{:keys [file line text]} findings]
        (println (format "  %s:%d\n    %s" file line text)))
      (println "\nEach must either cite its receipt, or say it is UNPROVEN.")))
  (if (empty? dangling)
    (println "every receipt CITATION resolves to a receipt, lane scenario or proof case that exists")
    (do
      (println (format "\n%d citation(s) name something that does not exist:\n" (count dangling)))
      (doseq [{:keys [file line name]} dangling]
        (println (format "  %s:%d\n    cites `%s`, which is not a receipt, lane scenario or proof case" file line name)))
      (println "\nEither write the receipt, or stop citing it.")))
  (when (and strict? (or (seq findings) (seq dangling))) (System/exit 1)))
