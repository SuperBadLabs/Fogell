;; fogell_adapter.bb — drive Fogell via the fogell-run wrapper (bench clone only).
;; Process model: one cold process per build (~0.1 s .NET floor) — no daemon exists.
;; DURABILITY: none. FogellSide.run does not journal (Wave 2 journal is not wired
;; into the executor); numbers are comparable to Jenkins PERFORMANCE_OPTIMIZED only.
(ns fogell-adapter
  (:require [babashka.fs :as fs]
            [babashka.process :refer [sh]]
            [clojure.string :as str]))

(def bin (str (System/getProperty "user.home") "/trifecta-bench/fogell-publish/fogell-run/fogell-run"))
(def ws-root (str (System/getProperty "user.home") "/trifecta-bench/fws"))

(defn- parse-run-output
  "fogell-run prints the receipt per-side shape:
     result:         success
     workspace-hash: <sha256>
     output (2 lines):
       | line1
       | line2"
  [out]
  (let [lines (str/split-lines out)
        grab (fn [k] (some #(when (str/starts-with? % k)
                              (str/trim (subs % (count k)))) lines))]
    {:result (grab "result:")
     :workspace-hash (grab "workspace-hash:")
     :output (->> lines
                  (filter #(str/starts-with? % "  | "))
                  (mapv #(subs % 4)))}))

(defn run-case!
  "Write src to a case file, run it in a cold process, time the whole invocation."
  [case-key src opts]
  (fs/create-dirs ws-root)
  (let [f (str ws-root "/" (name case-key) ".Jenkinsfile")
        job (str "bench-" (name case-key))]
    (spit f src)
    (let [t0 (System/nanoTime)
          r (sh {:continue true} bin ws-root job f)
          wall (/ (- (System/nanoTime) t0) 1e6)
          parsed (parse-run-output (:out r))]
      (merge parsed
             {:wall-ms wall
              :exit (:exit r)
              ;; exit 3 = engine refused the file (fail-closed) — distinct from
              ;; a pipeline that ran and failed (exit 1)
              :result (case (:exit r)
                        0 "success"
                        3 "engine-refused"
                        (or (:result parsed) "failure"))}))))

(defn start!
  "No persistent process. 'Startup' for a per-invocation engine = wall time of a
   one-echo build, cold — reported in seconds for comparability with the other
   engines' cold-start-to-ready (they include no build; label this in the report)."
  []
  (fs/create-dirs ws-root)
  (let [src "pipeline {\n  agent any\n  stages {\n    stage('one') { steps { echo 'up' } }\n  }\n}\n"
        r (run-case! :startup-probe src {})]
    (when (= "success" (:result r)) (/ (:wall-ms r) 1000.0))))

(defn stop! []
  (when (fs/exists? ws-root) (fs/delete-tree ws-root)))

(defn rss-mb
  "Peak RSS of a one-echo build via /usr/bin/time -v (no idle state exists)."
  []
  (fs/create-dirs ws-root)
  (let [f (str ws-root "/rss-probe.Jenkinsfile")]
    (spit f "pipeline {\n  agent any\n  stages {\n    stage('one') { steps { echo 'rss' } }\n  }\n}\n")
    (let [r (sh {:continue true} "/usr/bin/time" "-v" bin ws-root "rss-probe" f)
          line (some #(when (str/includes? % "Maximum resident set size") %)
                     (str/split-lines (:err r)))
          kb (some-> line (str/split #":") second str/trim parse-long)]
      (when kb (/ kb 1024.0)))))
