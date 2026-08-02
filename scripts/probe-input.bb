#!/usr/bin/env bb
;; FG-046b. The measurement behind the `input` approval semantics, kept in the
;; repo so the claim is rerunnable rather than remembered. Requires the pinned
;; lab (Jenkins 2.568.1) reachable at $JENKINS_URL, default http://127.0.0.1:18099.
;;
;;   scripts/probe-input.bb approve   — what does APPROVING print?
;;   scripts/probe-input.bb reject    — what does a human ABORT print?
;;   scripts/probe-input.bb restart   — does a PENDING prompt survive a restart?
;;
;; Results as measured 2026-08-01 (recorded in docs/adr/0005):
;;   approve  -> console goes straight from the prompt to the next step. No
;;               "Approved by ..." line. Result SUCCESS.
;;   reject   -> `Rejected`, then
;;               `org.jenkinsci.plugins.workflow.actions.ErrorAction$ErrorId: <uuid>`.
;;               Result ABORTED.
;;   restart  -> the pending action keeps the SAME hex id across a controller
;;               restart and is still approvable; result SUCCESS. The console
;;               gains `Pausing (shutting down)` / `Resuming build at ... after
;;               Jenkins restart` / `Ready to run at ...`.
;;
;; The restart mode needs the lab's container restart command, since a controller
;; restart is not something the REST API can be asked for honestly:
;;   RESTART_CMD='ssh luigi podman restart jenkins-lab' scripts/probe-input.bb restart
(require '[babashka.http-client :as http]
         '[babashka.process :refer [shell]]
         '[clojure.string :as str])

(def base (or (System/getenv "JENKINS_URL") "http://127.0.0.1:18099"))
(def mode (or (first *command-line-args*) "approve"))
(def job (str "probe-input-" mode))

(defn crumb-headers []
  (let [r (http/get (str base "/crumbIssuer/api/json"))
        cb (:body r)]
    ;; the crumb is bound to the SESSION that issued it: without carrying the
    ;; Set-Cookie back, every POST below is a 403
    (cond-> {(second (re-find #"\"crumbRequestField\":\"([^\"]+)\"" cb))
             (second (re-find #"\"crumb\":\"([^\"]+)\"" cb))}
      (get-in r [:headers "set-cookie"])
      (assoc "Cookie" (->> (get-in r [:headers "set-cookie"])
                           (#(if (string? %) [%] %))
                           (map #(first (str/split % #";")))
                           (str/join "; "))))))

;; a connection REFUSED is not an HTTP status — during a restart the tunnel
;; simply has nothing to talk to, and :throw false does not cover that
(defn try-get [path]
  (try (http/get (str base path) {:throw false :timeout 5000})
       (catch Exception _ {:status 0 :body ""})))

(def script
  (str "pipeline {\n  agent any\n  stages {\n    stage('gate') {\n      steps {\n"
       "        sh 'echo before-gate'\n"
       "        input message: 'Deploy?', ok: 'Ship it'\n"
       "        sh 'echo after-approval'\n"
       "      }\n    }\n  }\n}"))

(def xml
  (str "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies><properties/>"
       "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
       "<script>" (str/escape script {\< "&lt;" \> "&gt;" \& "&amp;"}) "</script><sandbox>true</sandbox></definition>"
       "<triggers/><disabled>false</disabled></flow-definition>"))

(defn pending-id []
  (let [r (try-get (str "/job/" job "/1/wfapi/nextPendingInputAction"))]
    (when (= 200 (:status r))
      (second (re-find #"\"id\":\"([^\"]+)\"" (:body r))))))

(defn await-pending [tries]
  (loop [i 0]
    (Thread/sleep 1000)
    (or (pending-id) (when (< i tries) (recur (inc i))))))

(let [hdrs (crumb-headers)]
  (http/post (str base "/job/" job "/doDelete") {:headers hdrs :throw false})
  (http/post (str base "/createItem?name=" job)
             {:headers (assoc hdrs "Content-Type" "application/xml") :body xml :throw false})
  (http/post (str base "/job/" job "/build") {:headers hdrs :throw false})

  (let [id (await-pending 60)]
    (println "pending input id:" id)

    (when (= mode "restart")
      (let [cmd (or (System/getenv "RESTART_CMD") "ssh luigi podman restart jenkins-lab")]
        (println "restarting the controller:" cmd)
        (shell cmd)
        (loop [i 0]
          (Thread/sleep 3000)
          (if (= 200 (:status (try-get "/api/json")))
            (println "controller serving again after ~" (* 3 (inc i)) "s")
            (when (< i 100) (recur (inc i)))))
        (let [id2 (await-pending 30)]
          (println "pending id AFTER restart:" id2)
          (println "SAME ID:" (= id id2)))))

    ;; the crumb is re-issued: a restarted controller does not know the old session
    (let [id (or (await-pending 30) id)
          hdrs (crumb-headers)
          path (if (= mode "reject") "abort" "proceedEmpty")]
      (when id
        (println path "->" (:status (http/post (str base "/job/" job "/1/input/" id "/" path)
                                               {:headers hdrs :throw false}))))))

  (loop [i 0]
    (Thread/sleep 2000)
    (let [r (try-get (str "/job/" job "/1/api/json"))]
      (cond
        (and (= 200 (:status r)) (re-find #"\"building\":false" (:body r)))
        (println "result:" (second (re-find #"\"result\":\"([A-Z_]+)\"" (:body r))))
        (< i 60) (recur (inc i))
        :else (println "TIMEOUT waiting for the build to finish"))))

  (println "---console---")
  (println (:body (try-get (str "/job/" job "/1/consoleText"))))
  (http/post (str base "/job/" job "/doDelete") {:headers (crumb-headers) :throw false}))
