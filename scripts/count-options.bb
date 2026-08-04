#!/usr/bin/env bb
;; FG-053. Count `options` DIRECTIVES across the corpus, by directive and by
;; scope, because a file count hides how cheap most of the work is and a naive
;; `rg -l` over-counts: the very first sampled file carries `//retry(3)`,
;; commented out, which a string search reports as a retry user.
;;
;; Scans with a tiny lexer rather than regexes: strings, line comments and block
;; comments are skipped, then `options` blocks are found by brace matching and
;; their directives read off. A `stage {` seen at an enclosing depth marks the
;; block STAGE-LEVEL — which matters now that FG-120 refuses that form.
(require '[babashka.fs :as fs] '[clojure.string :as str])

(defn scrub
  "Blank out COMMENTS ONLY, preserving offsets so brace matching stays aligned.

   An earlier version also blanked string literals, and one unmatched quote — an
   apostrophe in prose — blanked the REST OF THE FILE, so 33 files with an
   options block were reported as 10. Strings are left alone: they rarely
   carry braces that break matching, and a directive is read from the start of a
   line, where a string cannot be. `//` preceded by `:` is left alone too, so a
   URL inside a string does not eat the line."
  [^String s]
  (let [n (count s) out (StringBuilder.)]
    (loop [i 0]
      (if (>= i n)
        (.toString out)
        (let [c (.charAt s i)
              nx (when (< (inc i) n) (.charAt s (inc i)))
              prv (when (pos? i) (.charAt s (dec i)))]
          (cond
            (and (= c \/) (= nx \/) (not= prv \:))
            (let [e (or (str/index-of s "\n" i) n)]
              (dotimes [_ (- e i)] (.append out \space)) (recur e))

            (and (= c \/) (= nx \*))
            (let [e (if-let [j (str/index-of s "*/" (+ i 2))] (+ j 2) n)]
              (dotimes [_ (- e i)] (.append out \space)) (recur e))

            :else (do (.append out c) (recur (inc i)))))))))

(defn options-blocks
  "[[start end stage-level?] ...] for every `options { ... }` block."
  [^String s]
  (let [n (count s)]
    (loop [i 0, depth 0, stage-depths #{}, acc []]
      (if (>= i n)
        acc
        (let [c (.charAt s i)]
          (cond
            (= c \{) (recur (inc i) (inc depth) stage-depths acc)
            (= c \}) (recur (inc i) (dec depth) (disj stage-depths depth) acc)
            ;; `stage` on a WORD BOUNDARY, then optional space and `(` or `{`.
            ;; Matching the literal "stage " missed `stage('x')` — the form
            ;; Declarative actually uses — so every stage-level options block was
            ;; reported as pipeline-level and the "stage column is zero" claim
            ;; passed while unverified.
            (and (= c \s)
                 (str/starts-with? (subs s i (min n (+ i 5))) "stage")
                 (or (zero? i) (not (Character/isLetterOrDigit (.charAt s (dec i)))))
                 (let [r (str/triml (subs s (+ i 5) (min n (+ i 12))))]
                   (or (str/starts-with? r "(") (str/starts-with? r "{"))))
            (recur (inc i) depth (conj stage-depths (inc depth)) acc)
            (and (= c \o) (str/starts-with? (subs s i (min n (+ i 7))) "options")
                 (or (zero? i) (not (Character/isLetterOrDigit (.charAt s (dec i))))))
            ;; the brace must be the NEXT non-whitespace character. `index-of`
            ;; took the next `{` ANYWHERE, so a quoted `options` — scrub leaves
            ;; strings intact — started a block at some distant brace and swept
            ;; unrelated code in. That is the over-run that emitted `steps`,
            ;; `bat` and `try` as directives.
            (if-let [ob (let [j (loop [k (+ i 7)]
                                  (if (and (< k n) (Character/isWhitespace (.charAt s k)))
                                    (recur (inc k))
                                    k))]
                          (when (and (< j n) (= \{ (.charAt s j))) j))]
              (let [close (loop [j ob, d 0]
                            (cond (>= j n) n
                                  (= (.charAt s j) \{) (recur (inc j) (inc d))
                                  (= (.charAt s j) \}) (if (= d 1) j (recur (inc j) (dec d)))
                                  :else (recur (inc j) d)))]
                (recur (inc close) depth stage-depths
                       (conj acc [(inc ob) close (boolean (seq stage-depths))])))
              (recur (inc i) depth stage-depths acc))
            :else (recur (inc i) depth stage-depths acc)))))))

(when (empty? *command-line-args*)
  (println "usage: count-options.bb <corpus-directory>")
  (System/exit 2))

(let [files (->> (fs/list-dir (first *command-line-args*)) (filter fs/regular-file?) sort)
      rows (for [f files
                 :let [raw (slurp (str f))
                       s (scrub raw)]
                 [a b stage?] (options-blocks s)
                 ;; directive = the identifier opening a line inside the block
                 d (->> (subs s a b)
                        str/split-lines
                        (keep #(second (re-find #"^\s*([A-Za-z][A-Za-z0-9_]*)" %)))
                        distinct)]
             {:file (fs/file-name f) :directive d :stage stage?})]
  (println (format "corpus files: %d   files with an options block: %d"
                   (count files) (count (distinct (map :file rows)))))
  (println)
  (println (format "%-28s %8s %8s %8s" "directive" "files" "pipeline" "stage"))
  (doseq [[d rs] (->> rows (group-by :directive) (sort-by (comp - count val)))]
    (println (format "%-28s %8d %8d %8d"
                     d
                     (count (distinct (map :file rs)))
                     (count (distinct (map :file (remove :stage rs))))
                     (count (distinct (map :file (filter :stage rs))))))))
