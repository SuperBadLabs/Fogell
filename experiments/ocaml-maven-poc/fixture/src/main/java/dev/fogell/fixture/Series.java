package dev.fogell.fixture;

import java.util.ArrayList;
import java.util.List;

public final class Series {
    private Series() {}

    public static List<Integer> primesBelow(int limit) {
        List<Integer> primes = new ArrayList<>();
        for (int candidate = 2; candidate < limit; candidate++) {
            boolean prime = true;
            for (int divisor = 2; divisor * divisor <= candidate; divisor++) {
                if (candidate % divisor == 0) { prime = false; break; }
            }
            if (prime) primes.add(candidate);
        }
        return primes;
    }
}
