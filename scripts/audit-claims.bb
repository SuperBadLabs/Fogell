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
            :when (str/includes? line "MEASURED")
            ;; The receipt must be cited by THIS claim's own contiguous comment block, not
            ;; merely somewhere within twenty lines. A fixed window let a receipt named by a
            ;; NEIGHBOURING claim satisfy this one, so `--strict` could pass with an
            ;; unbacked claim sitting next to a backed one — defeating the per-claim
            ;; guarantee the check exists to give.
            :let [comment? (fn [l] (re-find #"^\s*(///|//)" l))
                  v (vec lines)
                  start (loop [k i] (if (and (pos? k) (comment? (v (dec k)))) (recur (dec k)) k))
                  stop (loop [k i] (if (and (< (inc k) (count v)) (comment? (v (inc k)))) (recur (inc k)) k))
                  block (str/join " " (subvec v start (inc stop)))
                  named (filter #(str/includes? block %) receipts)]
            :when (empty? named)]
        {:file (str (fs/relativize root f)) :line (inc i)
         :text (str/trim (subs line 0 (min 100 (count line))))})]

  (println (format "MEASURED claims: %d source files scanned, %d receipts available"
                   (count sources) (count receipts)))
  (if (empty? findings)
    (println "every MEASURED claim names a receipt that exists")
    (do
      (println (format "\n%d claim(s) name NO receipt — a reader cannot verify these:\n" (count findings)))
      (doseq [{:keys [file line text]} findings]
        (println (format "  %s:%d\n    %s" file line text)))
      (println "\nEach must either cite its receipt, or say it is UNPROVEN.")))
  (when (and strict? (seq findings)) (System/exit 1)))
