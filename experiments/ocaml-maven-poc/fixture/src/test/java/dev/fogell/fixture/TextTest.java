package dev.fogell.fixture;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import org.junit.Test;

public class TextTest {
    @Test public void countsWords() { assertEquals(Integer.valueOf(2), Text.wordCounts("Fogell fogell build").get("fogell")); }
    @Test public void palindromeIgnoresPunctuation() { assertTrue(Text.palindrome("A man, a plan, a canal: Panama!")); }
    @Test public void emptyIsPalindrome() { assertTrue(Text.palindrome("")); }
    @Test public void resourceIsOnClasspath() throws IOException {
        try (var input = getClass().getResourceAsStream("message.txt")) {
            assertEquals("hello from the packaged resource\n", new String(input.readAllBytes(), StandardCharsets.UTF_8));
        }
    }
}
