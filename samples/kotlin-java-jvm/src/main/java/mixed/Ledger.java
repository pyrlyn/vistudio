package mixed;

public final class Ledger {
    private final String owner;
    private int total;

    public Ledger(String owner) {
        this.owner = owner;
    }

    public String getOwner() {
        return owner;
    }

    public int getTotal() {
        return total;
    }

    public void add(int amount) {
        int before = total;
        total = before + amount;
    }

    public int applyKotlinRule(int amount) {
        int doubled = MixedMainKt.doubleIt(amount);
        total += doubled;
        return total;
    }
}
