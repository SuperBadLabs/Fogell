#!/usr/bin/env bb
;; FG-052. Push every SCM-marked case (first line `//// SCM JOB ////`) to the
;; fixture repo as branch case/<stem> with the body at /Jenkinsfile — the SAME
;; bytes both engines consume. IDEMPOTENT for real: the remote branch's current
;; Jenkinsfile is compared to the desired body and nothing is pushed when they
;; already agree; when content DID change, the commit is stamped with fixed
;; dates so the sha is a function of content+parent, not of when the sync ran —
;; sealed receipts embed the sha and must not churn on rerun.
(require '[babashka.fs :as fs]
         '[babashka.process :refer [shell sh]]
         '[clojure.string :as str])

(def url (or (System/getenv "FOGELL_SCM_URL") "git://100.105.179.51/repo.git"))
(def marker "//// SCM JOB ////")

(def scm-cases
  (for [f (fs/glob "differential/cases" "*.Jenkinsfile")
        :let [content (slurp (str f))]
        :when (str/starts-with? content marker)]
    {:stem (str/replace (fs/file-name f) #"\.Jenkinsfile$" "")
     :body (if-let [i (str/index-of content "\n")]
             (subs content (inc i))
             (do (println (str "ERROR: " (fs/file-name f) " is marker-only (no body)"))
                 (System/exit 1)))}))

(when (seq scm-cases)
  (let [work (str (fs/create-temp-dir {:prefix "fogell-scm-sync"}))]
    (shell {:dir work :out :string :err :string} "git" "clone" "-q" url ".")
    (doseq [{:keys [stem body]} scm-cases]
      (let [branch (str "case/" stem)
            current (let [r (sh {:dir work :continue true} "git" "show" (str "origin/" branch ":Jenkinsfile"))]
                      (when (zero? (:exit r)) (:out r)))
            main-head (str/trim (:out (sh {:dir work} "git" "rev-parse" "origin/main")))
            branch-parent (let [r (sh {:dir work :continue true} "git" "rev-parse" (str "origin/" branch "^"))]
                            (when (zero? (:exit r)) (str/trim (:out r))))]
        (if (and (= current body) (= branch-parent main-head))
          nil ; already in agreement — nothing moves, the sealed sha stays put
          (do
            (shell {:dir work :out :string :err :string}
                   "git" "checkout" "-q" "-B" branch "origin/main")
            (spit (fs/file work "Jenkinsfile") body)
            (shell {:dir work :out :string :err :string} "git" "add" "Jenkinsfile")
            (shell {:dir work
                    :out :string :err :string
                    :extra-env {"GIT_AUTHOR_DATE" "2026-01-01T00:00:00Z"
                                "GIT_COMMITTER_DATE" "2026-01-01T00:00:00Z"}}
                   "git" "-c" "user.email=harness@fogell" "-c" "user.name=fogell-harness"
                   "commit" "-qm" (str "sync: " stem))
            (shell {:dir work :out :string :err :string} "git" "push" "-qf" "origin" branch)
            (println (str "synced " branch))))))
    (fs/delete-tree work)))
