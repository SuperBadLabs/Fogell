#!/usr/bin/env bb
;; FG-052. Push every SCM-marked case (first line `//// SCM JOB ////`) to the
;; fixture repo as branch case/<stem> with the body at /Jenkinsfile — the SAME
;; bytes both engines consume: Jenkins obtains them from the SCM, the CLI hands
;; them (plus the ScmSpec) to the Fogell side. Idempotent: force-push only when
;; the content changed. Fails loudly; a half-synced lane is a broken harness.
(require '[babashka.fs :as fs]
         '[babashka.process :refer [shell sh]]
         '[clojure.string :as str])

(def url (or (System/getenv "FOGELL_SCM_URL") "git://100.105.179.51/repo.git"))
(def cases (fs/glob "differential/cases" "*.Jenkinsfile"))
(def marker "//// SCM JOB ////")

(def scm-cases
  (for [f cases
        :let [content (slurp (str f))]
        :when (str/starts-with? content marker)]
    {:stem (str/replace (fs/file-name f) #"\.Jenkinsfile$" "")
     :body (subs content (inc (str/index-of content "\n")))}))

(when (seq scm-cases)
  (let [work (str (fs/create-temp-dir {:prefix "fogell-scm-sync"}))]
    (shell {:dir work :out :string :err :string} "git" "clone" "-q" url ".")
    (doseq [{:keys [stem body]} scm-cases]
      (let [branch (str "case/" stem)]
        (shell {:dir work :out :string :err :string}
               "git" "checkout" "-q" "-B" branch "origin/main")
        (spit (fs/file work "Jenkinsfile") body)
        (shell {:dir work :out :string :err :string} "git" "add" "Jenkinsfile")
        (let [dirty (-> (sh {:dir work} "git" "status" "--porcelain") :out str/blank? not)
              upstream (-> (sh {:dir work :continue true} "git" "rev-parse" (str "origin/" branch)) :exit zero?)]
          (when (or dirty (not upstream))
            (shell {:dir work :out :string :err :string}
                   "git" "-c" "user.email=harness@fogell" "-c" "user.name=fogell-harness"
                   "commit" "-qam" (str "sync: " stem))
            (shell {:dir work :out :string :err :string}
                   "git" "push" "-qf" "origin" branch)
            (println (str "synced " branch))))))
    (fs/delete-tree work)))
