package mixed;

public final class JavaEntry {
    private JavaEntry() {
    }

    public static String describe(Ledger ledger) {
        String owner = ledger.getOwner();
        return owner + ":" + ledger.getTotal();
    }

    public static void main(String[] args) {
        Ledger ledger = new Ledger("Java");
        int total = ledger.applyKotlinRule(5);
        System.out.println(describe(ledger) + " total=" + total);
    }
}
