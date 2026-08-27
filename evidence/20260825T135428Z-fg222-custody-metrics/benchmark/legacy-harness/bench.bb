#!/usr/bin/env bb
;; bench.bb — trifecta benchmark driver: Jenkins 2.568.1 oracle vs Fogell vs McLoving.
;; Runs ON the benchmark host (luigi). Orchestrated from HeMan over ssh.
;;
;; Adapter protocol (a plain map):
;;   :name            keyword
;;   :durability      string label — the guarantee the run provides; "none" if not durable
;;   :start!          (fn [] -> seconds-to-ready double, or nil on failure)
;;   :stop!           (fn [])
;;   :rss-mb          (fn [] -> double)
;;   :run-case!       (fn [case-key src opts] -> {:result "success"|"failure"|...
;;                                               :wall-ms double
;;                                               :output [lines] (optional)
;;                                               :workspace-hash str (optional)})
;;   src is Jenkinsfile text for :jenkins/:fogell, the IR-twin dir for :mcloving.
;;
;; Usage: bb bench.bb <engines> <suites> [--iters N] [--out DIR]
;;   engines: comma list of jenkins,fogell,fogell-everystep,mcloving
;;   suites:  comma list of startup,idle-rss,echo-e2e,step-ladder,per-step,parallel,correctness,kill-recovery
(load-file (str (babashka.fs/parent *file*) "/stats.bb"))
(load-file (str (babashka.fs/parent *file*) "/cases.bb"))
(load-file (str (babashka.fs/parent *file*) "/jenkins_adapter.bb"))
(load-file (str (babashka.fs/parent *file*) "/fogell_adapter.bb"))
(load-file (str (babashka.fs/parent *file*) "/mcloving_adapter.bb"))

(ns bench
  (:require [babashka.fs :as fs]
            [babashka.process]
            [cheshire.core :as json]
            [clojure.string :as str]
            [stats] [cases]
            [jenkins-adapter :as jk]
            [fogell-adapter :as fg]
            [mcloving-adapter :as mc]))

(def adapters
  {:jenkins
   {:name :jenkins :durability "MAX_SURVIVABILITY (pipeline default, ~6.9 fsyncs/step)"
    :start! jk/start! :stop! jk/stop! :rss-mb jk/rss-mb
    :run-case! (fn [ck src opts]
                 (let [job (str "bench-" (name ck))]
                   (jk/upsert-job! job src)
                   (let [r (jk/run-build! job)]
                     (cond-> r
                       (and (:capture-workspace opts) (:build-num r))
                       (assoc :output (clojure.string/split-lines
                                       (jk/console-text job (:build-num r))))))))}
   :jenkins-perfopt
   {:name :jenkins-perfopt :durability "PERFORMANCE_OPTIMIZED (what the Fogell differential pins)"
    :start! jk/start! :stop! jk/stop! :rss-mb jk/rss-mb
    :run-case! (fn [ck src _]
                 (let [job (str "bench-po-" (name ck))]
                   (jk/upsert-job! job src :durability "PERFORMANCE_OPTIMIZED")
                   (jk/run-build! job)))}
   :fogell
   {:name :fogell :durability "NONE — journal exists but is not wired into the run path"
    :start! fg/start! :stop! fg/stop! :rss-mb fg/rss-mb
    :run-case! (fn [ck src opts] (fg/run-case! ck src opts))}
   :mcloving
   {:name :mcloving :max-steps 128
    :durability "per-stage durable, fenced exactly-once terminal state; logs committed post-step"
    :start! mc/start! :stop! mc/stop! :rss-mb mc/rss-mb
    :run-case! (fn [ck src opts] (mc/run-case! ck src opts))}})

;; --- suites -----------------------------------------------------------------

(defn suite-startup [ad iters]
  {:suite :startup :unit "s"
   :samples (vec (for [_ (range iters)]
                   (do ((:stop! ad))
                       (let [s ((:start! ad))] (or s -1.0)))))})

(defn suite-idle-rss [ad _]
  ((:start! ad))
  (Thread/sleep 5000)
  {:suite :idle-rss :unit "MB" :samples [((:rss-mb ad))]})

(defn suite-echo-e2e [ad iters]
  ((:start! ad))
  (let [src (cases/echo-1stage)
        _ (dotimes [_ 3] ((:run-case! ad) :echo-1stage src {}))   ; warmup
        runs (vec (for [_ (range iters)] ((:run-case! ad) :echo-1stage src {})))]
    {:suite :echo-e2e :unit "ms"
     :failures (count (remove #(#{"SUCCESS" "success"} (:result %)) runs))
     :samples (mapv :wall-ms runs)}))

(defn suite-step-ladder [ad _]
  ((:start! ad))
  (let [ks [50 100 200 250 251 300 400 600 1000]]
    {:suite :step-ladder :unit "result-per-k"
     :rows (vec (for [k ks]
                  (let [r ((:run-case! ad) :echo-ladder (cases/echo-ladder k) {:k k})]
                    {:k k :result (:result r) :wall-ms (:wall-ms r)})))}))

(defn suite-per-step [ad iters]
  ((:start! ad))
  (let [k (min 200 (:max-steps ad 200))
        base (vec (for [_ (range iters)] (:wall-ms ((:run-case! ad) :echo-1stage (cases/echo-1stage) {}))))
        lad  (vec (for [_ (range iters)] (:wall-ms ((:run-case! ad) :sh-ladder (cases/sh-ladder k) {:k k}))))]
    {:suite :per-step :unit "ms/step" :k k
     :durability (:durability ad)
     :baseline (stats/summarize base)
     :ladder (stats/summarize lad)
     :ms-per-step (when (and (seq base) (seq lad))
                    (/ (- (stats/median lad) (stats/median base)) (double k)))}))

(defn suite-parallel [ad iters]
  ((:start! ad))
  (let [src (cases/parallel-fanout 8 10)
        runs (vec (for [_ (range iters)] ((:run-case! ad) :parallel-fanout src {:b 8 :m 10})))]
    {:suite :parallel :unit "ms" :shape "8x10 sh"
     :failures (count (remove #(#{"SUCCESS" "success"} (:result %)) runs))
     :samples (mapv :wall-ms runs)}))

(defn suite-correctness [ad _]
  ((:start! ad))
  (let [r ((:run-case! ad) :workspace-write (cases/workspace-write) {:capture-workspace true})]
    {:suite :correctness
     :result (:result r)
     :output (:output r)
     :workspace-hash (:workspace-hash r)}))

(defn suite-kill-recovery [ad _]
  ;; Engine-specific hooks: adapter may expose :kill-mid-build! and :recover!
  (if-let [kmb (:kill-mid-build! ad)]
    (do ((:start! ad)) (kmb))
    {:suite :kill-recovery :skipped (str "no kill hook for " (name (:name ad)))}))

(def suites
  {:startup suite-startup :idle-rss suite-idle-rss :echo-e2e suite-echo-e2e
   :step-ladder suite-step-ladder :per-step suite-per-step :parallel suite-parallel
   :correctness suite-correctness :kill-recovery suite-kill-recovery})

;; --- main -------------------------------------------------------------------

(defn parse-args [args]
  (let [[engines-s suites-s & rest] args
        opts (apply hash-map rest)]
    {:engines (mapv keyword (str/split (or engines-s "jenkins") #","))
     :suites  (mapv keyword (str/split (or suites-s "echo-e2e") #","))
     :iters   (parse-long (get opts "--iters" "20"))
     :out     (get opts "--out" (str (fs/parent *file*) "/../results"))}))

(defn -main [& args]
  (let [{:keys [engines suites-ks iters out] :as cfg} (parse-args args)
        suites-ks (:suites cfg)
        stamp (stats/now-ms)
        results
        (vec
         (for [ek engines
               :let [ad (adapters ek)]
               sk suites-ks
               :let [f (suites sk)]]
           (do (binding [*out* *err*] (println (format "== %s / %s" (name ek) (name sk))))
               (let [r (try (let [r (f ad iters)]
                              (assoc r :summary (when (:samples r) (stats/summarize (:samples r)))))
                            (catch Exception e {:suite sk :error (str e)}))]
                 (assoc r :engine ek)))))]
    (doseq [ek engines] (when-let [s (:stop! (adapters ek))] (try (s) (catch Exception _))))
    (fs/create-dirs out)
    (let [file (str out "/trifecta-" stamp ".json")]
      (spit file (json/generate-string {:timestamp stamp :host (str/trim (:out (babashka.process/sh "hostname")))
                                        :config cfg :results results} {:pretty true}))
      (println file))))

(apply -main *command-line-args*)
