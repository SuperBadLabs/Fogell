;; stats.bb — shared statistics + result recording for trifecta-bench
(ns stats)

(defn percentile [xs p]
  (let [s (vec (sort xs)) n (count s)]
    (when (pos? n)
      (nth s (min (dec n) (int (Math/floor (* p n))))))))

(defn median [xs] (percentile xs 0.5))

(defn mean [xs]
  (when (seq xs) (/ (reduce + 0.0 xs) (count xs))))

(defn summarize [xs*]
  (let [xs (filter number? xs*)]
    (when (seq xs)
    {:n      (count xs)
     :median (median xs)
     :mean   (some-> (mean xs) (->> (format "%.2f")) parse-double)
     :p95    (percentile xs 0.95)
     :min    (apply min xs)
     :max    (apply max xs)
     :dropped-non-numeric (- (count xs*) (count xs))})))

(defn now-ms [] (System/currentTimeMillis))

(defn nano-timer []
  (let [t0 (System/nanoTime)]
    (fn [] (/ (- (System/nanoTime) t0) 1e6))))
