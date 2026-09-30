package dev.fogell.fixture;

import java.util.LinkedHashMap;
import java.util.Map;

public final class Text {
    private Text() {}

    public static Map<String, Integer> wordCounts(String input) {
        Map<String, Integer> counts = new LinkedHashMap<>();
        for (String word : input.toLowerCase().split("[^a-z0-9]+")) {
            if (!word.isEmpty()) counts.merge(word, 1, Integer::sum);
        }
        return counts;
    }

    public static boolean palindrome(String input) {
        String cleaned = input.toLowerCase().replaceAll("[^a-z0-9]", "");
        return cleaned.contentEquals(new StringBuilder(cleaned).reverse());
    }
}
