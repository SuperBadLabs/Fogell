package dev.fogell.fixture;

import static org.junit.Assert.assertEquals;
import org.junit.Test;

public class ArithmeticTest {
    @Test public void squareSum() { assertEquals(385, Arithmetic.sumOfSquares(10)); }
    @Test public void emptySquareSum() { assertEquals(0, Arithmetic.sumOfSquares(0)); }
    @Test public void gcd() { assertEquals(6, Arithmetic.gcd(54, 24)); }
    @Test public void negativeGcd() { assertEquals(6, Arithmetic.gcd(-54, 24)); }
}
