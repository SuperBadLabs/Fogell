#!/usr/bin/env bb
;; FG-104b. A comment that names a mechanism the code no longer has.
;;
;; Three of these in one day, all mine, all caught by a reviewer rather than by a
;; check: a lane comment explaining that a symlink alias works because the path
;; "resolves" (round 5 had replaced path derivation with an identity carried in
;; the journal); a host comment telling the reader the walker "promotes it (see
;; CommitInputAnswer)" after that hook was deleted; and a comment pointing at a
;; `requireStable` parameter the same commit removed.
;;
;; `audit-claims.bb` cannot see this class. It asks whether a MEASURED claim
;; names a receipt — a different question entirely, and one a stale identifier
;; passes trivially.
;;
;; The check: for every F# BINDING of four or more characters this diff DELETED
;; — `let`/`member`/`type`/`override`/`default`/`and` declarations and
;; PascalCase record fields — is it still named in a comment that survived? Line comments and
;; NESTED `(* ... *)` blocks both.
;;
;; F# BINDINGS, not identifiers in general, and the header said the looser thing
;; until a reviewer read it against the extractor. A deleted shell function, a
;; `bb` def, a YAML key or a step name in a lane script is NOT extracted, so a
;; comment naming one survives this audit silently. Comments in those files ARE
;; searched — the gap is what gets collected from the diff, not where it looks.
;;
;; That sentence was true of the INTENT and false of the code until the
;; extraction was restricted to `.fs`/`.fsi`/`.fsx`: the field arm had been
;; collecting `StageName:` out of shell scripts and failing the gate on them.
;; A scope comment that no mechanism enforces is a wish.
;; Widening it means a fixture per language, which is worth doing the day one of
;; those defects lands; asserting it in prose before then is how a checker comes
;; to be trusted for work it does not do.
;;
;; The length floor is a real limit, stated because the header said "every
;; identifier" until a reviewer deleted `let foo` and watched the audit report
;; `0 identifier(s) removed` and exit green. It is kept rather than removed:
;; `x`, `i`, `id`, `ctx` occur inside ordinary English in comments, and this
;; checker's whole value is that a report means something. A gate that cries
;; wolf on prose is a gate someone turns off. The boundary has its own fixture
;; in `prove-stale-refs`, so widening it later has to be a decision rather than
;; an accident. That is mechanical, so it is a script rather than a
;; rule someone remembers. It is deliberately narrow — deletions only, comments
;; only — because the alternative is a linter nobody can keep green.
;;
;;   usage: scripts/audit-stale-refs.bb [base-ref]     (default origin/main)
;;          --strict exits non-zero on any surviving reference

(require '[babashka.process :refer [shell]] '[clojure.string :as str] '[clojure.java.io :as io])

;; COMMENT TEXT, found by tracking block-comment state rather than by matching
;; line prefixes. `(* ... *)` spans lines and NESTS, so its interior lines carry
;; no marker at all and a prefix match cannot see them even in principle — the
;; reviewer deleted a binding, left `(* staleGateValue is explained here *)`, and
;; the blocking checker exited 0 on the exact class it advertises. Reproduced in
;; a scratch repo, not asserted.
;;
;; Over-approximating is deliberate: `https://x` inside a string reads as a
;; comment here. This checker's errors should fall on the side of asking a human
;; to look, never on the side of silence.
(defn- scan-line
  "[comment-start-index depth-after] for one line, given the block depth it
   begins with. nil start means the line holds no comment.

   A CHARACTER SCAN, not a token count. Counting `(*` and `*)` occurrences
   treats them as comment delimiters wherever they appear — so `let syntax =
   \"(*\"` opened a block comment that never closed, every later line in the file
   read as comment text, and surviving declarations below it were filtered out of
   the definition scan while ordinary code entered the comment index. One inert
   token in a string could make the blocking gate fail on unrelated code."
  [^String l depth]
  (let [n (.length l)]
    (loop [i 0, d depth, in-str false, start (when (pos? depth) 0)]
      (if (>= i n)
        [start d]
        (let [c (.charAt l i)
              c2 (when (< (inc i) n) (.charAt l (inc i)))]
          (cond
            ;; inside a block comment: only its own delimiters matter, and F#
            ;; block comments NEST
            (pos? d)
            (cond
              (and (= c \*) (= c2 \))) (recur (+ i 2) (dec d) false (or start i))
              (and (= c \() (= c2 \*)) (recur (+ i 2) (inc d) false (or start i))
              :else (recur (inc i) d false (or start i)))

            ;; inside a string: nothing is a delimiter until it closes. Strings
            ;; are treated as line-local, which is right for the single-line F#
            ;; literals this scan cares about and bounds the damage of an odd
            ;; quote in a shell script to its own line.
            in-str
            (cond
              (and (= c \\) c2) (recur (+ i 2) d true start)
              (= c \") (recur (inc i) d false start)
              :else (recur (inc i) d true start))

            :else
            (cond
              (= c \") (recur (inc i) d true start)
              (and (= c \/) (= c2 \/)) [(or start i) d]
              (and (= c \;) (= c2 \;)) [(or start i) d]
              (= c \#) [(or start i) d]
              (and (= c \() (= c2 \*)) (recur (+ i 2) (inc d) false (or start i))
              :else (recur (inc i) d false start))))))))

(defn- comment-spans
  "[[line-no text whole?] ...] for every line that is, or begins, a comment."
  [lines]
  (loop [[l & more] lines, n 1, depth 0, acc []]
    (if (nil? l)
      acc
      (let [inside? (pos? depth)
            [idx depth'] (scan-line l depth)
            ;; ENTIRELY a comment — as opposed to code with a trailing one.
            ;; The distinction matters for the definition scan: `let x = 1 // n`
            ;; is a definition, while an interior block-comment line is not,
            ;; even when it happens to be shaped like `Field: string`.
            whole? (or inside?
                       (and (some? idx)
                            (str/blank? (subs l 0 idx))))
            text (when (some? idx) (subs l idx))]
        (recur more (inc n) depth'
               (if text (conj acc [n text whole?]) acc))))))

;; THE BASE MUST RESOLVE, and this script shipped without checking it: `git diff`
;; ran with :continue true, a bad revision went to stderr, stdout came back empty
;; and the audit reported "nothing stale" and exited 0. A typo'd ref — or a CI
;; clone without `origin/main` — turned a BLOCKING gate green. That is precisely
;; the failure this project named a corollary about (a checker must NAME ITS
;; TARGET, never inherit or assume it), committed inside the checker meant to
;; enforce it. Caught in review before it merged.
;; An F# identifier may END in `'` (`state'`, `loop''`), which `\b` cannot close
;; against: `'` is not a word character, so there is no boundary between it and a
;; following space. The extractor allowed `'` from the start, so `let state' = ...`
;; was captured, reported as gone, and then silently failed to match its own
;; surviving comment — the audit exiting 0 while the defect it names sat in the
;; tree. Found by the pre-push reviewer, who reproduced it in a scratch repo
;; rather than asserting it. Rust's regex engine has no lookaround, so the
;; boundary is an explicit class plus end-of-line.
(def fsharp-boundary "($|[^A-Za-z0-9_'])")

;; THE TWO PATTERNS ARE BUILT FROM THESE, not written twice. They had drifted
;; twice already — the extractor learned about record fields on the `{` line and
;; the surviving-definition check did not, so a field that MOVED onto the brace
;; line was reported as deleted and any honest comment about it failed the build.
;; The same shape of half-fix hit block comments one commit earlier. Two regexes
;; describing one grammar will diverge; sharing the pieces makes it structural
;; rather than a thing to remember.
(def ^:private binding-core
  ;; modifiers that PRECEDE the keyword, the keyword, modifiers that FOLLOW it,
  ;; and an optional `receiver.` — everything between the line start and the name.
  ;;
  ;; `and` is a KEYWORD here, not only a prefix. F# recursive declarations begin
  ;; a line with it — `and CallTarget =`, `and private evalProp st recv = ...` —
  ;; and this repository has 17 of them across src/. Allowing `and` only BEFORE
  ;; another keyword meant deleting one added nothing to `removed`, so a comment
  ;; naming it passed a blocking audit. Found by a reviewer who went and counted
  ;; them in the tree rather than reasoning about the grammar.
  ;;
  ;; EVERY GROUP IS NON-CAPTURING, and the first version of this shared
  ;; definition was not. The extractor keeps all capture groups, so `member`,
  ;; `let` and the modifiers were themselves collected as deleted identifiers:
  ;; delete `member _.staleGateValue`, leave an unrelated `// team member
  ;; rotation notes`, and the audit failed the build on the word "member". A
  ;; false positive that BLOCKS pushes, introduced by the refactor that was
  ;; supposed to make this safer.
  "(?:(?:static|abstract|override|default|and)\\s+)*(?:let|member|type|override|default|and)\\s+(?:(?:private|internal|public|mutable|rec|inline|new|val)\\s+)*(?:[A-Za-z_][A-Za-z0-9_']*\\.)?")
(def ^:private field-lead
  ;; a record field, which this codebase writes indented, on the brace line
  ;; (`{ Root: string }`), carrying an attribute
  ;; (`[<JsonPropertyName "node_id">] NodeId: string`, Contracts.fs) or `mutable`
  ;; (`{ mutable Steps: int`, Interpreter.fs). The first three shapes were
  ;; handled and the last two were not, so deleting either contributed nothing
  ;; to `removed` and a comment naming it passed the blocking audit clean.
  "\\{?\\s*(?:\\[<[^>]*>\\]\\s*)?(?:mutable\\s+)?")

(let [args *command-line-args*
      strict? (some #{"--strict"} args)
      base (or (first (remove #{"--strict"} args)) "origin/main")
      _ (let [r (shell {:out :string :err :string :continue true} "git" "rev-parse" "--verify" "--quiet" (str base "^{commit}"))]
          (when-not (zero? (:exit r))
            (println (format "stale-reference audit: base ref %s does not resolve — refusing to report a clean tree it never compared against" base))
            (System/exit 2)))
      dr (apply shell {:out :string :err :string :continue true} "git" "diff" "-U0" base "--"
                (filterv #(.isDirectory (java.io.File. %)) ["src" "tools" "scripts" "tests"]))
      _ (when-not (zero? (:exit dr))
          (println (format "stale-reference audit: `git diff %s` failed: %s" base (str/trim (or (:err dr) ""))))
          (System/exit 2))
      diff (:out dr)

      ;; identifiers on REMOVED lines: F# let/member/type bindings and the
      ;; record fields this codebase uses as its hook surface. Deliberately not
      ;; a general symbol extractor — a broad net here reports the whole diff.
      ;; EXTRACTION IS F#-ONLY, and the scope comment said so while the code did
      ;; not: the diff covers scripts/, and the PascalCase field arm matches a
      ;; YAML-ish `StageName: old script step` in a shell script perfectly well.
      ;; Deleting one with a surviving `# StageName is documented here` FAILED
      ;; the blocking gate — a false positive outside the documented scope,
      ;; guarded by a sentence that described the intent rather than the code.
      ;;
      ;; The old path (`--- a/...`) is what the removed lines belong to, so a
      ;; wholly deleted .fs file is still read; `+++` would be /dev/null there.
      ;; Comments in scripts are still SEARCHED — only collection is narrowed.
      removed (->> (str/split-lines (or diff ""))
                   (reduce (fn [{:keys [file acc]} l]
                             (cond
                               (str/starts-with? l "--- ")
                               {:file (str/replace (subs l 4) #"^a/" "") :acc acc}

                               (and (str/starts-with? l "-")
                                    (not (str/starts-with? l "---"))
                                    file
                                    (re-find #"\.(fs|fsi|fsx)$" file))
                               {:file file :acc (conj acc l)}

                               :else {:file file :acc acc}))
                           {:file nil :acc []})
                   :acc
                   ;; F# puts MODIFIERS between the keyword and the name, and the
                   ;; first draft allowed only `private` — so `let mutable OldGate`
                   ;; captured "mutable" and `let rec` was missed entirely, while
                   ;; the gate's comment claimed deleted definitions were reported.
                   ;;
                   ;; Then, having been fixed once, it was STILL blind to the form
                   ;; this codebase writes most: members carry a RECEIVER. The
                   ;; reviewer reproduced it — deleting `member _.staleMemberValue`
                   ;; with a comment naming it exited clean, because `_` is not
                   ;; [A-Za-z] so nothing matched at all, and `member this.Value`
                   ;; captured "this". Hence the optional `receiver.` below.
                   ;;
                   ;; `static`/`abstract` need no entry: they precede `member`, which
                   ;; still anchors the match. `override`/`default` DO, because F#
                   ;; lets them stand alone — `override this.Foo() = ...` has no
                   ;; `member` keyword to anchor on, and the planted fixture caught
                   ;; that the moment it existed.
                   ;;
                   ;; ANCHORED to the start of the removed line, which also
                   ;; settles a false POSITIVE: deleting a COMMENT that happens to
                   ;; contain binding syntax — `// let GhostGateValue = old docs`
                   ;; — was extracted as a deleted binding, so removing stale
                   ;; documentation could fail the build while nothing was
                   ;; actually removed. After the `-`, a comment marker precedes
                   ;; the keyword and no longer matches.
                   ;;
                   ;; The field arm is PASCALCASE ONLY, which is a real limit and
                   ;; is stated rather than implied: `{ staleGateValue: string }`
                   ;; deleted with its comment left behind passes silently. Every
                   ;; record label in this codebase is PascalCase (measured: zero
                   ;; lowercase labels across src/ and tools/), and widening the
                   ;; arm to lowercase would collect things like `mutable cache:`
                   ;; as field names — false positives, which block pushes, to
                   ;; cover a form nothing here uses. Pinned by a fixture so the
                   ;; day someone writes a lowercase label, widening it is a
                   ;; decision.
                   ;;
                   ;; The record-field arm allows an optional `{`: this codebase
                   ;; routinely writes the first field on the brace line
                   ;; (`{ Root: string }`), and requiring `-` then whitespace then
                   ;; the name meant deleting that field added NOTHING to the set,
                   ;; so a comment naming the deleted hook sailed through.
                   ;;
                   ;; A checker with a silent blind spot is the defect it exists to
                   ;; catch; every form below has a planted fixture in
                   ;; `prove-stale-refs`, which is the only reason this was three
                   ;; findings rather than three years.
                   (mapcat #(re-seq (re-pattern (format "^\\-\\s*%s([A-Za-z][A-Za-z0-9_']{3,})|^\\-\\s*%s([A-Z][A-Za-z0-9_']{3,})\\s*:"
                                                       binding-core field-lead)) %))
                   (mapcat rest)
                   (remove nil?)
                   set)

      ;; ...that are GONE from the tree entirely. An identifier that merely moved
      ;; is not stale, and reporting it would train people to ignore this script.
      ;; only the roots that EXIST — a scratch tree (or a repo mid-restructure)
      ;; otherwise fills the report with rg errors that can hide a real one
      roots (filterv #(.isDirectory (java.io.File. %)) ["src" "tools" "scripts" "tests"])
      scanned (vec (for [f (mapcat #(file-seq (io/file %)) roots)
                         :when (and (.isFile f)
                                    (not (re-find #"/(bin|obj|\.git)/" (.getPath f))))
                         :let [content (try (slurp f) (catch Exception _ nil))]
                         :when content]
                     [(.getPath f) (comment-spans (str/split-lines content))]))
      comment-index (vec (for [[path spans] scanned, [n text _] spans] [path n text]))
      ;; line numbers that are ENTIRELY comment, per file. `still-defined?`
      ;; consults this instead of re-deciding from the matched text: an interior
      ;; block-comment line carries NO marker, so a prefix test called
      ;; `(* ...\n   StaleGateValue: string\n*)` a surviving record field and the
      ;; deleted identifier never reached the stale-comment check. One comment
      ;; model for both scans, rather than two that disagree at the edges.
      whole-comment (into {} (for [[path spans] scanned]
                               [path (set (keep (fn [[n _ whole?]] (when whole? n)) spans))]))
      ;; rg exits 0 on a match, 1 on none, and >1 on a REAL ERROR (bad pattern,
      ;; unreadable path, missing binary). Ignoring the code made every error
      ;; read as "no match" — an unreadable tree would have reported every
      ;; identifier gone, and a bad pattern would have reported none, both
      ;; while exiting 0/1 as though the audit had run. A blocking checker that
      ;; cannot search must say CANNOT PROVE, which is the same lesson the base
      ;; ref taught two rounds earlier.
      rg! (fn [what args]
            (let [r (apply shell {:out :string :err :string :continue true} "rg" args)]
              (when (> (:exit r) 1)
                (println (format "stale-reference audit: rg failed while %s (exit %d): %s"
                                 what (:exit r) (str/trim (or (:err r) ""))))
                (System/exit 2))
              (:out r)))
      ;; A COMMENT IS NOT A DEFINITION, and this search could not tell the
      ;; difference. Delete `member OldHook` and leave `// member OldHook used
      ;; to ...` behind, and the unanchored pattern matched the comment, called
      ;; the identifier still-defined, dropped it from `gone`, and exited clean
      ;; — defeated by the exact artefact it exists to find. The planted proofs
      ;; missed it because their comments name the identifier WITHOUT the
      ;; keyword. Caught in review.
      ;;
      ;; Filtering happens here rather than in the regex on purpose: "is this
      ;; line a comment" is a question about the whole line, and a regex that
      ;; tried to express it alongside the binding grammar would be the kind of
      ;; thing nobody can check by reading.
      ;;
      ;; The pattern is also ANCHORED now. Unanchored, a surviving STRING
      ;; — `let keep = "let StaleGateValue"` — read as a definition, and the
      ;; whole-line comment filter could not help because the line is code. A
      ;; definition begins its line (after indentation and any
      ;; static/abstract/override prefix); anything matching mid-line is text
      ;; that merely looks like one.
      still-defined? (fn [id]
                       (->> (rg! (str "searching for a surviving definition of " id)
                                 (concat ["-n" "--no-heading"
                                          (format "^\\s*%s%s%s|^\\s*%s%s\\s*:"
                                                  binding-core id fsharp-boundary field-lead id)]
                                         (remove #{"scripts"} roots)))
                            str/split-lines
                            (remove str/blank?)
                            ;; rg -n --no-heading prints `path:line:content`
                            (keep #(let [parts (str/split % #":" 3)]
                                     (when (= 3 (count parts))
                                       [(nth parts 0) (parse-long (nth parts 1)) (nth parts 2)])))
                            (remove (fn [[path n _]]
                                      (contains? (get whole-comment path #{}) n)))
                            seq
                            boolean))

      gone (remove still-defined? removed)

      ;; ...but still named in a surviving COMMENT. Built once, from the files
      ;; themselves, because `rg` cannot carry block-comment depth across lines.
      hits (for [id gone
                 :let [pat (re-pattern (str "\\b" (java.util.regex.Pattern/quote id) fsharp-boundary))]
                 [path n text] comment-index
                 :when (re-find pat text)]
             [id (format "%s:%d:%s" path n (str/trim text))])]

  (println (format "stale-reference audit: %d identifier(s) removed vs %s, %d fully gone"
                   (count removed) base (count gone)))
  (if (empty? hits)
    (println "no surviving comment names a deleted identifier")
    (do
      (println (format "\n%d comment(s) name an identifier this diff deleted:\n" (count hits)))
      (doseq [[id line] hits] (println (format "  %-28s %s" id (str/trim line))))
      (println "\nEach is either a comment to update or a deletion to reconsider.")
      (when strict? (System/exit 1)))))
