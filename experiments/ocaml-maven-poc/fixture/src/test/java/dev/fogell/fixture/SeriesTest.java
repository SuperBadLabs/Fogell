package dev.fogell.fixture;

import static org.junit.Assert.assertEquals;
import java.util.List;
import org.junit.Test;

public class SeriesTest {
    @Test public void primes() { assertEquals(List.of(2, 3, 5, 7, 11, 13, 17, 19), Series.primesBelow(20)); }
    @Test public void noPrimes() { assertEquals(List.of(), Series.primesBelow(2)); }
}
