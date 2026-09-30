package dev.fogell.fixture;

public final class Arithmetic {
    private Arithmetic() {}

    public static long sumOfSquares(int limit) {
        long sum = 0;
        for (int n = 1; n <= limit; n++) sum += (long) n * n;
        return sum;
    }

    public static int gcd(int a, int b) {
        a = Math.abs(a);
        b = Math.abs(b);
        while (b != 0) {
            int remainder = a % b;
            a = b;
            b = remainder;
        }
        return a;
    }
}
