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
;; The check: for every identifier this diff DELETED, is it still named in a
;; comment that survived? That is mechanical, so it is a script rather than a
;; rule someone remembers. It is deliberately narrow — deletions only, comments
;; only — because the alternative is a linter nobody can keep green.
;;
;;   usage: scripts/audit-stale-refs.bb [base-ref]     (default origin/main)
;;          --strict exits non-zero on any surviving reference

(require '[babashka.process :refer [shell]] '[clojure.string :as str])

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
      removed (->> (str/split-lines (or diff ""))
                   (filter #(and (str/starts-with? % "-") (not (str/starts-with? % "---"))))
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
                   ;; A checker with a silent blind spot is the defect it exists to
                   ;; catch; every form below has a planted fixture in
                   ;; `prove-stale-refs`, which is the only reason this was three
                   ;; findings rather than three years.
                   (mapcat #(re-seq #"(?:let|member|type|override|default)\s+(?:(?:private|internal|public|mutable|rec|inline|new|val)\s+)*(?:[A-Za-z_][A-Za-z0-9_']*\.)?([A-Za-z][A-Za-z0-9_']{3,})|^\-\s+([A-Z][A-Za-z0-9_']{3,})\s*:" %))
                   (mapcat rest)
                   (remove nil?)
                   set)

      ;; ...that are GONE from the tree entirely. An identifier that merely moved
      ;; is not stale, and reporting it would train people to ignore this script.
      ;; only the roots that EXIST — a scratch tree (or a repo mid-restructure)
      ;; otherwise fills the report with rg errors that can hide a real one
      roots (filterv #(.isDirectory (java.io.File. %)) ["src" "tools" "scripts" "tests"])
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
      still-defined? (fn [id]
                       (not (str/blank?
                             (rg! (str "searching for a surviving definition of " id)
                                  (concat ["-l" (format "(let|member|type|override|default)\\s+((private|internal|public|mutable|rec|inline|new|val)\\s+)*([A-Za-z_][A-Za-z0-9_']*\\.)?%s%s|^\\s+%s\\s*:" id fsharp-boundary id)]
                                          (remove #{"scripts"} roots))))))

      gone (remove still-defined? removed)

      ;; ...but still named in a surviving COMMENT.
      hits (for [id gone
                 :let [out (rg! (str "searching for comments naming " id)
                                (concat ["-n" "--no-heading"
                                         (format "(//|///|;;|#).*\\b%s%s" id fsharp-boundary)]
                                        roots))]
                 line (remove str/blank? (str/split-lines (or out "")))]
             [id line])]

  (println (format "stale-reference audit: %d identifier(s) removed vs %s, %d fully gone"
                   (count removed) base (count gone)))
  (if (empty? hits)
    (println "no surviving comment names a deleted identifier")
    (do
      (println (format "\n%d comment(s) name an identifier this diff deleted:\n" (count hits)))
      (doseq [[id line] hits] (println (format "  %-28s %s" id (str/trim line))))
      (println "\nEach is either a comment to update or a deletion to reconsider.")
      (when strict? (System/exit 1)))))
