;; cases.bb — parameterized pipeline sources, one generator per benchmark case.
;; Jenkins and Fogell consume the Jenkinsfile text directly; McLoving runs a
;; hand-authored IR twin of the same work (see cases/mcloving/).
(ns cases
  (:require [clojure.string :as str]))

(defn decl
  "Wrap stages-body in a minimal declarative pipeline."
  [stages]
  (str "pipeline {\n  agent any\n  stages {\n" stages "  }\n}\n"))

(defn echo-1stage []
  (decl "    stage('one') { steps { echo 'bench-mark-line' } }\n"))

(defn echo-ladder
  "One stage, k in-engine echo steps."
  [k]
  (decl (str "    stage('ladder') { steps {\n"
             (str/join (for [i (range k)] (str "      echo 'L" i "'\n")))
             "    } }\n")))

(defn sh-ladder
  "One stage, k subprocess steps (`sh 'true'`). The durable per-step probe."
  [k]
  (decl (str "    stage('ladder') { steps {\n"
             (str/join (for [_ (range k)] "      sh 'true'\n"))
             "    } }\n")))

(defn parallel-fanout
  "b parallel branches, each with m sh steps."
  [b m]
  (decl (str "    stage('fan') {\n      parallel {\n"
             (str/join
              (for [i (range b)]
                (str "        stage('b" i "') { steps {\n"
                     (str/join (for [_ (range m)] "          sh 'true'\n"))
                     "        } }\n")))
             "      }\n    }\n")))

(defn workspace-write
  "Deterministic workspace output — the correctness-gate case."
  []
  (decl (str "    stage('w') { steps {\n"
             "      sh 'printf alpha > a.txt'\n"
             "      sh 'mkdir -p d && printf beta > d/b.txt'\n"
             "      echo 'wrote files'\n"
             "    } }\n")))

(defn sleepy
  "k sh steps of `sleep secs` each — the kill-recovery workload."
  [k secs]
  (decl (str "    stage('slow') { steps {\n"
             (str/join (for [i (range k)] (str "      sh 'sleep " secs " && echo S" i "'\n")))
             "    } }\n")))

(def registry
  "case-key -> {:gen fn-or-value :desc}"
  {:echo-1stage     {:gen echo-1stage :desc "single stage, single echo — end-to-end floor"}
   :echo-ladder     {:gen echo-ladder :desc "k in-engine steps, one stage — step ceiling probe"}
   :sh-ladder       {:gen sh-ladder :desc "k subprocess steps — durable per-step cost"}
   :parallel-fanout {:gen parallel-fanout :desc "b branches x m sh steps"}
   :workspace-write {:gen workspace-write :desc "deterministic files — correctness gate"}
   :sleepy          {:gen sleepy :desc "slow steps — kill/recovery workload"}})
