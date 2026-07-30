#!/usr/bin/env bb
;; FG-002d. List each automated review round for a PR with its reviewed commit and
;; the findings NEW in that round.
;;
;; Exists because of a specific failure: on PR #13 I filtered review comments with
;; a `created_at` cutoff in the wrong timezone, so every poll re-showed round 1. I
;; read that as the reviewer re-posting stale findings, said so, and merged over
;; NINE unread findings — two of which let success-only post arms run on a failed
;; build. Never eyeball timestamps again; group by round and diff the titles.
;;
;;   usage: scripts/review-rounds.bb <pr-number> [owner/repo]

(require '[babashka.process :refer [shell]]
         '[cheshire.core :as json]
         '[clojure.string :as str])

(let [[pr repo] *command-line-args*
      repo (or repo "SuperBadLabs/Fogell")]
  (when-not pr
    (println "usage: scripts/review-rounds.bb <pr-number> [owner/repo]")
    (System/exit 2))

  (defn gh [path]
    ;; REVIEW FIX (Codex, PR #14 round 4), and it is pointed: `gh api --paginate`
    ;; emits ONE JSON VALUE PER PAGE, so `parse-string` on the combined stream sees
    ;; only the first page. This checker exists precisely to stop me under-reporting
    ;; review findings, and it could have reported "0 NEW" while omitting newer
    ;; comments — the failure it was written to prevent, reproduced inside the
    ;; prevention.
    ;;
    ;; Codex suggested `--slurp`; this gh (2.45.0) does not have that flag, so the
    ;; concatenated values are read as a SEQUENCE instead, which works on every
    ;; version. Verified against a PR with >1 page by lowering per_page.
    (->> (shell {:out :string} "gh" "api" (str "repos/" repo path) "--paginate")
         :out
         java.io.StringReader.
         json/parsed-seq
         (mapcat identity)
         vec))

  (let [comments (gh (str "/pulls/" pr "/comments"))
        reviews  (gh (str "/pulls/" pr "/reviews"))
        ;; A "round" is a distinct comment timestamp minute — the bots post a
        ;; whole review at once.
        ;; REVIEW FIX (Codex, PR #14 round 6): identity was the TRUNCATED title, so two
        ;; findings sharing their first 72 characters collapsed and a round could
        ;; report "0 NEW" — which this script itself calls safe to skip. Second time a
        ;; flaw of the very class this tool prevents has been found inside it. Identity
        ;; is the full normalised title; truncation is for display only.
        full-title (fn [c]
                   (-> (get c "body")
                       (str/replace #"!\[P\d Badge\]\(https[^)]*\)" "")
                       (str/replace #"[*#]|<sub>|</sub>" "")
                       str/split-lines
                       (->> (remove str/blank?))
                       first
                       (or "")
                       str/trim))
        title-of (fn [c] (let [t (full-title c)] (subs t 0 (min 72 (count t)))))
        rounds (->> comments
                    (group-by #(subs (get % "created_at") 0 16))
                    (sort-by key))
        shas (->> reviews
                  (keep (fn [r]
                          (when-let [m (re-find #"Reviewed commit:.*?`([0-9a-f]{7,})`" (or (get r "body") ""))]
                            [(subs (get r "submitted_at") 0 16) (subs (second m) 0 10)])))
                  (into {}))]

    (println (format "PR #%s — %d comments across %d round(s)" pr (count comments) (count rounds)))
    (loop [[[stamp cs] & more] rounds
           seen #{}
           n 1]
      (when stamp
        (let [titles (map full-title cs)
              fresh  (remove seen titles)]
          (println (format "\nround %d  %sZ  commit %s  (%d comment(s), %d NEW)"
                           n stamp (get shas stamp "?") (count cs) (count fresh)))
          (doseq [c cs
                  :let [t (full-title c)]]
            (println (format "  %-4s %-28s %s"
                             (if (seen t) "seen" "NEW")
                             (str (last (str/split (get c "path") #"/")) ":"
                                  (or (get c "line") (get c "original_line")))
                             (title-of c))))
          (recur more (into seen titles) (inc n)))))
    (println "\nEvery NEW line must be triaged before merging. A round with 0 NEW is the only safe one to skip.")))
