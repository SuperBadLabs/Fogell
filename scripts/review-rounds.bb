#!/usr/bin/env bb
;; FG-002d. List each automated review round for a PR with its reviewed commit and
;; the findings NEW in that round.
;;
;; Exists because of a specific failure: on PR #13 I filtered review comments with a
;; `created_at` cutoff in the wrong timezone, so every poll re-showed round 1. I read
;; that as the reviewer re-posting stale findings, said so, and merged over NINE
;; unread findings — two of which let success-only post arms run on a failed build.
;;
;; This script has itself been the subject of four review findings, every one of the
;; class it exists to prevent: it parsed only the first page of `--paginate`; it
;; deduplicated on a truncated title; then on a title without its location; and it
;; grouped rounds by wall-clock minute, which merges two reviews submitted in the same
;; minute and splits one review whose comments cross a boundary — misattributing
;; findings to the wrong commit. Rounds are now keyed by `pull_request_review_id`,
;; which is what actually defines a round.
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
    ;; `gh api --paginate` emits ONE JSON VALUE PER PAGE, so parse the concatenated
    ;; stream as a sequence. (`--slurp` would do it, but gh 2.45 has no such flag.)
    (->> (shell {:out :string} "gh" "api" (str "repos/" repo path) "--paginate")
         :out
         java.io.StringReader.
         json/parsed-seq
         (mapcat identity)
         vec))

  (let [comments (gh (str "/pulls/" pr "/comments"))
        reviews  (gh (str "/pulls/" pr "/reviews"))
        review-by-id (into {} (map (juxt #(get % "id") identity)) reviews)

        full-title (fn [c]
                     (-> (get c "body")
                         (str/replace #"!\[P\d Badge\]\(https[^)]*\)" "")
                         (str/replace #"[*#]|<sub>|</sub>" "")
                         str/split-lines
                         (->> (remove str/blank?))
                         first
                         (or "")
                         str/trim))
        ;; Identity is path + line + full title: a truncated title collides, and so
        ;; does a full title repeated in another file.
        identity-of (fn [c]
                      [(get c "path")
                       (or (get c "line") (get c "original_line"))
                       (full-title c)])
        display (fn [c] (let [t (full-title c)] (subs t 0 (min 72 (count t)))))

        ;; A ROUND is a review, not a minute.
        rounds (->> comments
                    (group-by #(get % "pull_request_review_id"))
                    (sort-by (fn [[rid cs]]
                               [(or (get-in review-by-id [rid "submitted_at"])
                                    (get (first cs) "created_at"))
                                (str rid)])))]

    (println (format "PR #%s — %d comments across %d review(s)" pr (count comments) (count rounds)))
    (loop [[[rid cs] & more] rounds, seen #{}, n 1]
      (when rid
        (let [r      (get review-by-id rid)
              sha    (some-> (or (get r "commit_id") "") (subs 0 (min 10 (count (or (get r "commit_id") "")))))
              when'  (or (get r "submitted_at") (get (first cs) "created_at"))
              who    (get-in r ["user" "login"] "?")
              ids    (map identity-of cs)
              fresh  (remove seen ids)]
          (println (format "\nreview %d  %s  %s  commit %s  (%d comment(s), %d NEW)"
                           n when' who (if (str/blank? (str sha)) "?" sha) (count cs) (count fresh)))
          (doseq [c cs]
            (println (format "  %-4s %-28s %s"
                             (if (seen (identity-of c)) "seen" "NEW")
                             (str (last (str/split (get c "path") #"/")) ":"
                                  (or (get c "line") (get c "original_line")))
                             (display c))))
          (recur more (into seen ids) (inc n)))))
    (println "\nEvery NEW line must be triaged before merging. A review with 0 NEW is the only safe one to skip.")))
