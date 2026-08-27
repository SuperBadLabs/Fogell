#!/usr/bin/env bb
;; FG-162 plus the FG-224 accounting closure. Compatibility tokens are derived from
;; the committed ledger, and the one live BOARD ACCOUNTING line is derived from the
;; canonical Wave ticket rows. The latter closes the exact gap that let prose advance
;; from 192 to 193 rows while the tables already contained 207.
;;
;; Canonical ticket accounting semantics:
;;   - rows begin `| FG-` between `## Wave 0` and `## Standing risks`;
;;   - Waves 0, 1, 2, 3, 3.5, 3.6 and 4..9 each appear once and are nonempty;
;;   - ids are unique, priorities are P0..P3, and statuses are the board's four-state
;;     vocabulary (DONE, TODO, PARTIAL, BLOCKED);
;;   - MOVED/SUPERSEDED/Retired rows exist at, and carry the status of, their target;
;;   - open means every legal status except DONE;
;;   - exactly one anchored `BOARD ACCOUNTING (derived)` line publishes the totals.
;;
;; WHAT IT DOES NOT CHECK:
;;   - arbitrary prose numbers: volatile totals belong only in the anchored summary;
;;   - freshness of the compatibility ledger (`generate-scorecard.bb --check` owns it);
;;   - quoted historical tier tokens, which are deliberately exempt.
;;
;; usage: scripts/audit-board-numbers.bb [board-file ledger-file]

(require '[babashka.fs :as fs] '[clojure.string :as str])

(def legal-priorities #{"P0" "P1" "P2" "P3"})
(def legal-statuses #{"DONE" "TODO" "PARTIAL" "BLOCKED"})
(def expected-wave-labels ["0" "1" "2" "3" "3.5" "3.6" "4" "5" "6" "7" "8" "9"])
(def expected-wave-set (set expected-wave-labels))

(defn first-index [pred xs]
  (first (keep-indexed (fn [idx value] (when (pred value) idx)) xs)))

(defn cell [parts idx]
  (-> (nth parts idx "")
      (str/replace "**" "")
      str/trim))

(defn redirect-target [detail]
  (some-> (re-find #"(?i)^(?:MOVED to|SUPERSEDED by|Retired by)\s+(FG-\d+[a-z]?)" detail)
          second
          str/upper-case))

(let [root (str (fs/parent (fs/parent (fs/absolutize *file*))))
      [board-arg ledger-arg] *command-line-args*
      ledger-file (if ledger-arg (fs/file ledger-arg) (fs/file root "docs/COMPATIBILITY-LEDGER.tsv"))
      board-file (if board-arg (fs/file board-arg) (fs/file root "docs/EXECUTION_BOARD.md"))]

  (when-not (fs/exists? ledger-file)
    (println "FAIL: docs/COMPATIBILITY-LEDGER.tsv missing — board numbers cannot be derived")
    (System/exit 1))

  (when-not (fs/exists? board-file)
    (println (str "FAIL: board file not found: " (str board-file)))
    (System/exit 1))

  (let [tiers (->> (str/split-lines (slurp ledger-file))
                   (remove #(or (str/blank? %)
                                (str/starts-with? % "#")
                                (str/starts-with? % "file\t")))
                   (map #(second (str/split % #"\t" -1))))
        compatibility {"tier1" (count (filter #(= "1" %) tiers))
                       "tier3" (count (filter #(= "3" %) tiers))
                       "admitted" (count (filter #(= "admitted" %) tiers))}
        board (slurp board-file)
        lines (vec (str/split-lines board))
        wave-start (first-index #(str/starts-with? % "## Wave ") lines)
        standing-start (first-index #(= "## Standing risks" %) lines)
        valid-region? (and (some? wave-start)
                           (some? standing-start)
                           (< wave-start standing-start))
        wave-headings
        (if valid-region?
          (->> lines
               (keep-indexed
                (fn [idx line]
                  (when (and (<= wave-start idx) (< idx standing-start))
                    (when-let [[_ label] (re-find #"^## Wave ([^ ]+)" line)]
                      {:index idx :line-number (inc idx) :label label})))))
          [])
        wave-frequencies (frequencies (map :label wave-headings))
        row-lines (if valid-region?
                    (->> lines
                         (keep-indexed
                          (fn [idx line]
                            (when (and (< wave-start idx standing-start)
                                       (re-find #"^\|\s*FG-" line))
                              {:line-number (inc idx)
                               :line line
                               :wave (->> wave-headings
                                          (filter #(<= (:index %) idx))
                                          last
                                          :label)}))))
                    [])
        parsed-rows
        (mapv (fn [{:keys [line-number line wave]}]
                (let [parts (str/split line #"\|" -1)]
                  {:line-number line-number
                   :line line
                   :wave wave
                   :structural? (and (>= (count parts) 7)
                                     (boolean (re-matches #"FG-\d+[a-z]?" (cell parts 1))))
                   :id (cell parts 1)
                   :priority (cell parts 2)
                   :status (cell parts 3)
                   :redirect-target (redirect-target (cell parts 5))}))
              row-lines)
        structural-rows (filter :structural? parsed-rows)
        legal-rows (filter #(and (legal-priorities (:priority %))
                                 (legal-statuses (:status %)))
                           structural-rows)
        duplicates (->> structural-rows
                        (map :id)
                        frequencies
                        (filter (fn [[_ n]] (> n 1)))
                        (sort-by key))
        rows-by-id (into {} (map (juxt :id identity) structural-rows))
        rows-by-wave (frequencies (map :wave legal-rows))
        status-frequencies (frequencies (map :status legal-rows))
        open-rows (filter #(not= "DONE" (:status %)) legal-rows)
        open-priorities (frequencies (map :priority open-rows))
        accounting {"rows" (count legal-rows)
                    "DONE" (get status-frequencies "DONE" 0)
                    "open" (count open-rows)
                    "P0" (get open-priorities "P0" 0)
                    "P1" (get open-priorities "P1" 0)
                    "P2" (get open-priorities "P2" 0)
                    "P3" (get open-priorities "P3" 0)}
        summary-matches
        (re-seq #"(?m)^\*\*BOARD ACCOUNTING \(derived\): rows=(\d+); DONE=(\d+); open=(\d+); open P0–P3=(\d+) / (\d+) / (\d+) / (\d+)\.\*\*$"
                board)
        summary-values
        (when (= 1 (count summary-matches))
          (zipmap ["rows" "DONE" "open" "P0" "P1" "P2" "P3"]
                  (map parse-long (rest (first summary-matches)))))
        findings
        (concat
         (when-not valid-region?
           ["canonical Wave region is missing or ends before it starts"])

         (for [label expected-wave-labels
               :when (zero? (get wave-frequencies label 0))]
           (str "Wave " label " — expected heading is missing"))

         (for [[label n] (sort-by key wave-frequencies)
               :when (> n 1)]
           (str "Wave " label " — heading appears " n " times"))

         (for [label (sort (remove expected-wave-set (keys wave-frequencies)))]
           (str "Wave " label " — unexpected heading"))

         (for [label expected-wave-labels
               :when (and (= 1 (get wave-frequencies label 0))
                          (zero? (get rows-by-wave label 0)))]
           (str "Wave " label " — contains no legal canonical ticket row"))

         (for [{:keys [line-number structural?]} parsed-rows :when (not structural?)]
           (str "line " line-number " — malformed canonical ticket row"))

         (for [{:keys [line-number priority]} structural-rows
               :when (not (legal-priorities priority))]
           (str "line " line-number " — illegal priority " (pr-str priority)))

         (for [{:keys [line-number status]} structural-rows
               :when (not (legal-statuses status))]
           (str "line " line-number " — illegal status " (pr-str status)))

         (for [[id n] duplicates]
           (str id " — duplicate canonical id appears " n " times"))

         (for [{:keys [id redirect-target]} structural-rows
               :when (and redirect-target (nil? (get rows-by-id redirect-target)))]
           (str id " — redirect target " redirect-target " does not exist"))

         (for [{:keys [id status redirect-target]} structural-rows
               :let [target-row (get rows-by-id redirect-target)]
               :when (and redirect-target target-row (not= status (:status target-row)))]
           (str id " — redirect status " status " disagrees with " redirect-target
                " status " (:status target-row)))

         (case (count summary-matches)
           0 ["missing anchored BOARD ACCOUNTING (derived) summary"]
           1 []
           [(str "expected one anchored BOARD ACCOUNTING (derived) summary; found "
                 (count summary-matches))])

         (when summary-values
           (for [kind ["rows" "DONE" "open" "P0" "P1" "P2" "P3"]
                 :let [stated (get summary-values kind)
                       derived (get accounting kind)]
                 :when (not= stated derived)]
             (str "accounting " kind "=" stated
                  " — canonical Wave rows derive " kind "=" derived)))

         ;; Live tier2= claims are refused: ADR tier 2 is published as NOT ASSESSED.
         (->> (re-seq #"([\"]?)tier2=\*{0,2}(\d+)" board)
              (keep (fn [[_ q n]]
                      (when (not= q "\"")
                        (str "tier2=" n " — ADR tier 2 is NOT ASSESSED; no live claim may use this token")))))

         ;; Live compatibility tokens must match the generated ledger.
         (->> (re-seq #"([\"]?)(tier1|tier3|admitted)=\*{0,2}(\d+)\*{0,2}" board)
              (keep (fn [[_ q kind n]]
                      (let [want (get compatibility kind)]
                        (when (and (not= q "\"") (not= (parse-long n) want))
                          (str kind "=" n " — the ledger derives " kind "=" want)))))))]

    (if (seq findings)
      (do
        (println (str "BOARD-NUMBER AUDIT FAILED (" (count findings) "):"))
        (doseq [finding findings] (println "  " finding))
        (println "Fix the canonical rows or the one derived summary; regenerate the ledger only if its compatibility counts are wrong.")
        (System/exit 1))
      (println
       (str "board accounting consistent: rows=" (accounting "rows")
            " DONE=" (accounting "DONE")
            " open=" (accounting "open")
            " open-P0..P3=" (str/join "/" (map accounting ["P0" "P1" "P2" "P3"]))
            "; compatibility ledger: tier1=" (compatibility "tier1")
            " tier3=" (compatibility "tier3")
            " admitted=" (compatibility "admitted"))))))
